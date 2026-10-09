using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RuleForge.SolidWorks
{
    /// <summary>
    /// Montajı ve tüm alt belgelerini gezip <see cref="ModelSnapshot"/> üretir.
    /// Okuma amaçlıdır; modelde değişiklik yapmaz.
    /// </summary>
    public sealed class SnapshotExtractor
    {
        // Ağaçtaki klasörler ve kural için anlamsız sistem öğeleri.
        private static readonly HashSet<string> SkippedFeatureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "SurfaceBodyFolder", "SolidBodyFolder",
            "EnvFolder", "CommentsFolder", "EqnFolder", "FavoriteFolder", "SelectionSetFolder", "InkMarkupFolder",
            "MaterialFolder", "BlockFolder", "MateReferenceGroupFolder", "LiveSectionFolder", "MarkupCommentFolder",
            "OriginProfileFeature", "Reference", "ReferencePattern", "FtrFolder", "SubAtomFolder", "SubWeldFolder",
            "CutListFolder", "BurstFolder", "ExplodeLineProfileFeature", "GridFeature", "AnnotationFolder",
        };

        private static readonly Regex EquationLhs = new Regex("^\\s*\"([^\"]+)\"\\s*=", RegexOptions.Compiled);

        private readonly SwSession _session;

        public SnapshotExtractor(SwSession session)
        {
            _session = session;
        }

        public ModelSnapshot Extract(string assemblyPath, string? variantName = null)
        {
            assemblyPath = Path.GetFullPath(assemblyPath);
            var app = _session.App;
            var root = _session.Open(assemblyPath, readOnly: true);
            app.CommandInProgress = true;
            try
            {
                if (root is AssemblyDoc asm) asm.ResolveAllLightWeightComponents(false);

                var snapshot = new ModelSnapshot
                {
                    Name = variantName ?? Path.GetFileNameWithoutExtension(assemblyPath),
                    SourcePath = assemblyPath,
                    RootDocument = Path.GetFileName(assemblyPath),
                    SolidWorksVersion = _session.VersionLabel,
                };

                var docs = new Dictionary<string, ModelDoc2>(StringComparer.OrdinalIgnoreCase)
                {
                    [assemblyPath] = root,
                };

                var config = (Configuration)root.GetActiveConfiguration();
                if (config?.GetRootComponent3(true) is Component2 rootComponent)
                    WalkComponents(rootComponent, null, snapshot, docs);

                foreach (var kv in docs)
                    snapshot.Documents.Add(ReadDocument(kv.Value, kv.Key));

                MarkEquationDrivenDimensions(snapshot);
                if (root is AssemblyDoc) ReadMates(root, snapshot);
                return snapshot;
            }
            finally
            {
                app.CommandInProgress = false;
                _session.Close(assemblyPath);
            }
        }

        private static void WalkComponents(Component2 parent, string? parentPath, ModelSnapshot snapshot,
            Dictionary<string, ModelDoc2> docs)
        {
            if (!(parent.GetChildren() is object[] children)) return;
            foreach (Component2 child in children)
            {
                var path = child.Name2; // SolidWorks tam yol verir: "Alt-1/Parca-2"
                var file = child.GetPathName();
                bool suppressed = child.IsSuppressed();
                snapshot.Components.Add(new ComponentInfo
                {
                    Path = path,
                    Name = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path,
                    ParentPath = parentPath,
                    DocumentKey = Path.GetFileName(file),
                    Configuration = child.ReferencedConfiguration ?? string.Empty,
                    Suppressed = suppressed,
                    ExcludedFromBom = child.ExcludeFromBOM,
                    IsPatternInstance = child.IsPatternInstance(),
                });

                if (!suppressed && !string.IsNullOrEmpty(file) && !docs.ContainsKey(file) &&
                    child.GetModelDoc2() is ModelDoc2 model)
                    docs[file] = model;

                WalkComponents(child, path, snapshot, docs);
            }
        }

        private static DocumentInfo ReadDocument(ModelDoc2 doc, string path)
        {
            var info = new DocumentInfo
            {
                Key = Path.GetFileName(path),
                Path = path,
                Kind = KindOf(doc),
            };

            if (doc.GetConfigurationNames() is string[] configs) info.Configurations.AddRange(configs);
            if (doc.GetActiveConfiguration() is Configuration active) info.ActiveConfiguration = active.Name;

            ReadFeatures(doc, info);
            ReadEquations(doc, info);
            ReadProperties(doc.Extension.get_CustomPropertyManager(""), info.CustomProperties);
            if (!string.IsNullOrEmpty(info.ActiveConfiguration))
                ReadProperties(doc.Extension.get_CustomPropertyManager(info.ActiveConfiguration), info.ConfigurationProperties);
            return info;
        }

        private static DocumentKind KindOf(ModelDoc2 doc)
        {
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART: return DocumentKind.Part;
                case swDocumentTypes_e.swDocASSEMBLY: return DocumentKind.Assembly;
                case swDocumentTypes_e.swDocDRAWING: return DocumentKind.Drawing;
                default: return DocumentKind.Unknown;
            }
        }

        private static void ReadFeatures(ModelDoc2 doc, DocumentInfo info)
        {
            var seenDims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var feature = doc.FirstFeature() as Feature;
            while (feature != null)
            {
                ReadFeature(feature, info, seenDims, topLevel: true);
                feature = feature.GetNextFeature() as Feature;
            }
        }

        private static void ReadFeature(Feature feature, DocumentInfo info, HashSet<string> seenDims, bool topLevel)
        {
            var type = feature.GetTypeName2();
            if (SkippedFeatureTypes.Contains(type)) return;

            if (topLevel || type != "ProfileFeature")
            {
                info.Features.Add(new FeatureInfo
                {
                    Name = feature.Name,
                    Type = type,
                    Suppressed = IsSuppressed(feature),
                });
            }

            var display = feature.GetFirstDisplayDimension() as DisplayDimension;
            while (display != null)
            {
                var dim = display.GetDimension2(0);
                if (dim != null)
                {
                    // Parametre adı "D1@Sketch1" biçiminde; ModelDoc2.Parameter() ile geri bulunur.
                    var name = $"{dim.Name}@{feature.Name}";
                    if (seenDims.Add(name)) info.Dimensions.Add(ReadDimension(dim, name, feature.Name));
                }
                display = feature.GetNextDisplayDimension(display) as DisplayDimension;
            }

            // Alt özellikler (ör. Extrude altındaki Sketch) ölçülerin çoğunu taşır.
            var sub = feature.GetFirstSubFeature() as Feature;
            while (sub != null)
            {
                ReadFeature(sub, info, seenDims, topLevel: false);
                sub = sub.GetNextSubFeature() as Feature;
            }
        }

        private static bool IsSuppressed(Feature feature)
        {
            var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            return states != null && states.Length > 0 && states[0];
        }

        private static DimensionInfo ReadDimension(Dimension dim, string name, string featureName)
        {
            double system = dim.SystemValue;
            DimensionUnit unit;
            double value;
            switch ((swDimensionParamType_e)dim.GetType())
            {
                case swDimensionParamType_e.swDimensionParamTypeDoubleAngular:
                    unit = DimensionUnit.Degree;
                    value = system * 180.0 / Math.PI;
                    break;
                case swDimensionParamType_e.swDimensionParamTypeInteger:
                    unit = DimensionUnit.None;
                    value = system;
                    break;
                default:
                    unit = DimensionUnit.Millimeter;
                    value = system * 1000.0;
                    break;
            }
            return new DimensionInfo
            {
                Name = name,
                Feature = featureName,
                FullName = SafeFullName(dim),
                Value = Math.Round(value, 6),
                Unit = unit,
                IsDriven = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven,
            };
        }

        private static string? SafeFullName(Dimension dim)
        {
            try
            {
                return dim.FullName;
            }
            catch (Exception)
            {
                return null; // bazı eski sürümlerde/ölçü türlerinde desteklenmeyebilir
            }
        }

        private static void ReadEquations(ModelDoc2 doc, DocumentInfo info)
        {
            var mgr = doc.GetEquationMgr();
            if (mgr == null) return;
            int count = mgr.GetCount();
            for (int i = 0; i < count; i++)
            {
                var text = mgr.get_Equation(i);
                var m = EquationLhs.Match(text ?? string.Empty);
                var name = m.Success ? m.Groups[1].Value : string.Empty;
                info.Equations.Add(new EquationInfo
                {
                    Text = text ?? string.Empty,
                    Name = name,
                    // Ölçü adları her zaman '@' içerir ("D1@Sketch1"); global değişkenler içermez.
                    // (EquationMgr.GlobalVariable 2015 API'sinde yok; bu yöntem tüm sürümlerde çalışır.)
                    IsGlobalVariable = name.Length > 0 && name.IndexOf('@') < 0,
                    Value = SafeValue(mgr, i),
                });
            }
        }

        private static double? SafeValue(EquationMgr mgr, int i)
        {
            try
            {
                return mgr.get_Value(i);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ReadProperties(CustomPropertyManager? mgr, Dictionary<string, string> target)
        {
            if (mgr == null || !(mgr.GetNames() is object[] names)) return;
            foreach (string name in names)
            {
                mgr.Get4(name, false, out var raw, out var resolved);
                target[name] = string.IsNullOrEmpty(resolved) ? raw ?? string.Empty : resolved;
            }
        }

        /// <summary>
        /// Denklemin sol tarafındaki ölçüleri işaretler. Parça denklemi: "D1@Sketch1";
        /// montaj denklemi alt parçayı hedefleyebilir: "D1@Sketch1@Govde-1.Part".
        /// </summary>
        private static void MarkEquationDrivenDimensions(ModelSnapshot snapshot)
        {
            foreach (var doc in snapshot.Documents)
            foreach (var eq in doc.Equations.Where(e => !e.IsGlobalVariable && e.Name.Contains("@")))
            {
                var parts = eq.Name.Split('@');
                var target = doc;
                var dimName = eq.Name;
                if (parts.Length >= 3)
                {
                    dimName = parts[0] + "@" + parts[1];
                    var compName = parts[2];
                    var dot = compName.LastIndexOf('.');
                    if (dot > 0) compName = compName.Substring(0, dot);
                    var comp = snapshot.Components.FirstOrDefault(c => string.Equals(c.Name, compName, StringComparison.OrdinalIgnoreCase));
                    if (comp != null) target = snapshot.FindDocument(comp.DocumentKey) ?? doc;
                }
                var dim = target.FindDimension(dimName);
                if (dim != null) dim.DrivenByEquation = eq.Text;
            }
        }

        private static void ReadMates(ModelDoc2 root, ModelSnapshot snapshot)
        {
            var feature = root.FirstFeature() as Feature;
            while (feature != null)
            {
                if (feature.GetTypeName2() == "MateGroup")
                {
                    var mate = feature.GetFirstSubFeature() as Feature;
                    while (mate != null)
                    {
                        if (mate.GetSpecificFeature2() is Mate2 spec)
                        {
                            var info = new MateInfo
                            {
                                Name = mate.Name,
                                Type = ((swMateType_e)spec.Type).ToString().Replace("swMate", ""),
                                Suppressed = IsSuppressed(mate),
                            };
                            for (int i = 0; i < spec.GetMateEntityCount(); i++)
                            {
                                var comp = spec.MateEntity(i)?.ReferenceComponent;
                                if (comp != null) info.Components.Add(comp.Name2);
                            }
                            snapshot.Mates.Add(info);
                        }
                        mate = mate.GetNextSubFeature() as Feature;
                    }
                }
                feature = feature.GetNextFeature() as Feature;
            }
        }
    }
}
