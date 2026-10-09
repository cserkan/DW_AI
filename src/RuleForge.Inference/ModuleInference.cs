using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    public static partial class RuleInferencer
    {
        /// <summary>
        /// İki katmanlı çıkarım. Varyantlar tekrarlanan bir modülün kopyalarını içerir (ör. hat = 3 konveyör bölümü):
        /// <list type="bullet">
        /// <item>Kopyadan kopyaya değişen değerler (bölüm uzunluğu, rulo adedi...) kopya düzeyinde: her kopya bir örnek,
        /// girdileri tablo sütunları (satır girdileri) + genel girdiler + satır no + satır sayısı.</item>
        /// <item>Bir varyantın tüm kopyalarında aynı olan değerler (yükseklik, genişlik, açıklamalar...) ve hat montajının
        /// kendi değerleri varyant düzeyinde: girdileri genel girdiler + tablo özetleri (ilk satır, toplam, satır sayısı...).</item>
        /// </list>
        /// Sonuç tek kural seti: genel girdiler, bir tablo (satır girdileri) ve tablo kapsamlı kurallar.
        /// </summary>
        private static InferenceReport InferModules(ModuleSplit split, InferenceOptions options, ModelSnapshot? master)
        {
            var variants = split.Outer;
            var report = new InferenceReport { SampleCount = variants.Count };
            report.Notes.AddRange(split.Notes);
            var table = split.TableName;
            var instancesOf = variants.ToDictionary(v => v.Name, v => split.InstancesOf(v.Name).Select(i => i.Name).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var variantOf = split.InstanceInfo.ToDictionary(i => i.Name, i => i.Variant, StringComparer.OrdinalIgnoreCase);

            // 1–3) Kopyaların ve dış kısmın gözlemleri; kopya gözlemleri satır / genel diye ayrılır.
            var mo = ExtractModuleObservations(split, options, report.Notes);
            var instExtractor = mo.InstanceExtractor;
            var outerExtractor = mo.OuterExtractor;
            var rowObs = mo.Row;
            var variantObs = mo.Variant;
            bool IsInstanceObs(Observation o) => mo.IsInstanceObservation(o.Key);
            report.ObservationCount = mo.InstanceCount + mo.OuterCount;
            report.Changes = Changes(rowObs, split.Instances.Count).Concat(Changes(variantObs, variants.Count)).ToList();

            // 4) Satır girdileri: kopyadan kopyaya değişen değerleri en iyi açıklayanlar (ör. bölüm uzunluğu).
            bool blind = options.InputObservations.Count == 0 && variants.All(s => s.Inputs.Count == 0);
            var rowDrivers = DriverDetector.Detect(rowObs, split.Instances, options.Tolerance);
            var rowDriverObs = rowDrivers.Select(d => rowObs.First(o => o.Key == d.ObservationKey)).ToList();

            // Tablo özetleri: varyant düzeyindeki değerler satırların ilk/son/toplam... değerine ya da satır sayısına bağlı olabilir.
            var countName = table + "_Adet";
            var aggregates = new List<InputColumn>
            {
                new InputColumn(countName, ColumnKind.Number, variants.ToDictionary(v => v.Name, v => Value.Number(instancesOf[v.Name].Count), StringComparer.OrdinalIgnoreCase)),
            };
            for (int i = 0; i < rowDrivers.Count; i++)
            {
                var d = rowDrivers[i];
                var obs = rowDriverObs[i];
                bool numeric = obs.Values.Values.All(v => v.Kind == ValueKind.Number);
                var first = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                var last = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                var sum = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                var max = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                var min = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in variants)
                {
                    var vals = instancesOf[v.Name].Where(obs.Values.ContainsKey).Select(n => obs.Values[n]).ToList();
                    if (vals.Count == 0) continue;
                    first[v.Name] = vals[0];
                    last[v.Name] = vals[vals.Count - 1];
                    if (!numeric) continue;
                    var nums = vals.Select(x => x.AsNumber()).ToList();
                    sum[v.Name] = Value.Number(nums.Sum());
                    max[v.Name] = Value.Number(nums.Max());
                    min[v.Name] = Value.Number(nums.Min());
                }
                var kind = numeric ? ColumnKind.Number : ColumnKind.Category;
                aggregates.Add(new InputColumn(TableSymbols.First(d.Name), kind, first));
                aggregates.Add(new InputColumn(TableSymbols.Last(d.Name), kind, last));
                if (!numeric) continue;
                aggregates.Add(new InputColumn(TableSymbols.Sum(d.Name), kind, sum));
                aggregates.Add(new InputColumn(TableSymbols.Max(d.Name), kind, max));
                aggregates.Add(new InputColumn(TableSymbols.Min(d.Name), kind, min));
            }

            // 5) Genel girdiler (varyant düzeyinde): tablo özetleri yardımcı sütun olarak hazır.
            var globalDrivers = new List<DetectedDriver>();
            var globalSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (blind)
            {
                globalDrivers = DriverDetector.Detect(variantObs, variants, options.Tolerance, aggregates);
                foreach (var d in globalDrivers)
                {
                    globalSources[d.Name] = d.ObservationKey;
                    var obs = variantObs.First(o => o.Key == d.ObservationKey);
                    foreach (var v in variants)
                        if (obs.Values.TryGetValue(v.Name, out var value)) v.Inputs[d.Name] = value;
                }
            }
            else
            {
                foreach (var kv in options.InputObservations)
                {
                    var obs = variantObs.FirstOrDefault(o => string.Equals(o.Key, kv.Value, StringComparison.OrdinalIgnoreCase));
                    if (obs == null) continue;
                    globalSources[kv.Key] = obs.Key;
                    foreach (var v in variants)
                        if (obs.Values.TryGetValue(v.Name, out var value)) v.Inputs[kv.Key] = value;
                }
            }
            report.Drivers = globalDrivers.Concat(rowDrivers).ToList();
            if (blind && report.Drivers.Count > 0)
                report.Notes.Add("Girdi tablosu verilmediği için girdiler modelden tahmin edildi. Adlar modeldeki ölçü/özellik adlarıdır; " +
                                 "DriveWorks'teki gerçek girdilerle karşılaştırın.");

            var globalColumns = BuildInputColumns(variants, report.Inputs, report.Notes);
            var variantColumns = globalColumns.Concat(aggregates).ToList();

            // 6) Tablo tanımı: satır girdileri = satır sürücüleri.
            var counts = variants.Select(v => instancesOf[v.Name].Count).ToList();
            var tableDef = new TableDefinition
            {
                Name = table,
                Label = System.IO.Path.GetFileNameWithoutExtension(split.Document) + " kopyaları",
                Module = split.Document,
                ModuleComponent = split.ComponentPath,
                MinRows = counts.Min(),
                MaxRows = counts.Max(),
                Description = $"Her satır bir '{split.Document}' kopyası. Varyantlarda {counts.Min()}–{counts.Max()} satır görüldü.",
            };
            var rowColumns = new List<InputColumn>();
            for (int i = 0; i < rowDrivers.Count; i++)
            {
                rowColumns.Add(ColumnFor(rowDrivers[i].Name, rowDriverObs[i].Label,
                    new Dictionary<string, Value>(rowDriverObs[i].Values, StringComparer.OrdinalIgnoreCase), out var def));
                tableDef.Columns.Add(def);
            }
            report.Tables.Add(tableDef);

            // 7) Kopya düzeyi sütunlar: genel girdiler (kopyanın varyantından), satır girdileri, satır no, satır sayısı.
            var instColumns = new List<InputColumn>();
            foreach (var g in globalColumns)
                instColumns.Add(new InputColumn(g.Name, g.Kind, split.Instances
                    .Where(s => g.Values.ContainsKey(variantOf[s.Name]))
                    .ToDictionary(s => s.Name, s => g.Values[variantOf[s.Name]], StringComparer.OrdinalIgnoreCase)));
            instColumns.AddRange(rowColumns);
            instColumns.Add(new InputColumn(table + "_Sira", ColumnKind.Number,
                split.InstanceInfo.ToDictionary(i => i.Name, i => Value.Number(i.Row), StringComparer.OrdinalIgnoreCase)));
            instColumns.Add(new InputColumn(countName, ColumnKind.Number,
                split.InstanceInfo.ToDictionary(i => i.Name, i => Value.Number(instancesOf[i.Variant].Count), StringComparer.OrdinalIgnoreCase)));

            // 8) Kurallar: önce varyant düzeyi (genel değerler + hat montajı), sonra kopya düzeyi (satır değerleri).
            RuleTarget InstanceTarget(Observation o) => MapToMaster(o.Target, split.InstanceReference, instExtractor);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var masterValues = master == null ? null : MasterValues(split.InstanceReference, options, options.MatchByStructure != false);
            var variantCtx = new RuleContext
            {
                Observations = variantObs,
                Samples = variants,
                Columns = variantColumns,
                InputObservationKeys = new HashSet<string>(globalSources.Values, StringComparer.OrdinalIgnoreCase),
                InputSources = globalSources,
                TargetOf = o => IsInstanceObs(o) ? InstanceTarget(o) : OuterTarget(o, split, outerExtractor),
                ScopeOf = o => IsInstanceObs(o) ? table : null,
                Tolerance = options.Tolerance,
                UsedIds = usedIds,
                MasterValues = masterValues,
            };
            var needs = FindRules(report, variantCtx);

            var instCtx = new RuleContext
            {
                Observations = rowObs,
                Samples = split.Instances,
                Columns = instColumns,
                InputObservationKeys = new HashSet<string>(rowDrivers.Select(d => d.ObservationKey), StringComparer.OrdinalIgnoreCase),
                InputSources = rowDrivers.ToDictionary(d => d.Name, d => d.ObservationKey, StringComparer.OrdinalIgnoreCase),
                TargetOf = InstanceTarget,
                ScopeOf = _ => table,
                VariableScope = table,
                Tolerance = options.Tolerance,
                UsedIds = usedIds,
                MasterValues = masterValues,
            };
            var rowNeeds = FindRules(report, instCtx);

            // Açıklanamayanlarda kopya düzeyindeki değerleri varyanta göre grupla (okunabilirlik).
            foreach (var u in report.Unexplained.Where(u => u.Scope != null && u.Values.Keys.Any(k => variantOf.ContainsKey(k))))
                u.Values = u.Values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(kv => kv.Key, kv => kv.Value);

            foreach (var d in report.Drivers.Where(d => d.Question != null))
                needs.Add(new ProbeNeed { Priority = 2, Input = d.Name, Reason = d.Question! });
            DedupeNeeds(needs);
            DedupeNeeds(rowNeeds);
            report.Needs = needs.Concat(rowNeeds).ToList();
            report.SuggestedVariants = PlanWithRows(report, tableDef, globalColumns, rowColumns, aggregates, needs, rowNeeds);
            foreach (var v in variants)
            {
                report.InputValues[v.Name] = globalColumns.Where(c => c.Values.ContainsKey(v.Name))
                    .ToDictionary(c => c.Name, c => c.Values[v.Name], StringComparer.OrdinalIgnoreCase);
                report.RowValues[v.Name] = instancesOf[v.Name].Select(i => rowColumns.Where(c => c.Values.ContainsKey(i))
                    .ToDictionary(c => c.Name, c => c.Values[i], StringComparer.OrdinalIgnoreCase)).ToList();
            }

            report.Notes.Add($"'{table}' tablosu: her satır bir modül kopyası; satır girdileri {(tableDef.Columns.Count > 0 ? string.Join(", ", tableDef.Columns.Select(c => c.Name)) : "yok")}. " +
                             $"Formüllerde {countName} satır sayısı, {table}_Sira satır numarası, <sütun>_Ilk / _Son / _Toplam / _EnBuyuk / _EnKucuk tablo özetleridir.");
            if (variants.Count < 6)
                report.Notes.Add($"Sadece {variants.Count} varyant var; güven düşük. Girdi aralığının uçlarını kapsayan 8–15 varyant önerilir.");
            return report;
        }

        /// <summary>Modül kopyalarının ve dış kısmın gözlemleri (çıkarım ve çapraz doğrulama aynı anahtarları kullanır).</summary>
        internal sealed class ModuleObservations
        {
            public ObservationExtractor InstanceExtractor = null!;
            public ObservationExtractor OuterExtractor = null!;

            /// <summary>Tüm kopya gözlemleri (değerler kopya örnek adlarıyla).</summary>
            public List<Observation> Instance = new List<Observation>();

            /// <summary>Dış kısmın gözlemleri (önek olmadan).</summary>
            public List<Observation> Outer = new List<Observation>();

            /// <summary>Kopyadan kopyaya değişen (satır) gözlemler; değerler kopya örnek adlarıyla.</summary>
            public List<Observation> Row = new List<Observation>();

            /// <summary>Varyant düzeyi: kopyalarda hep aynı olan gözlemler (varyant adlarıyla) + dış kısmın gözlemleri ("dis|" önekli).</summary>
            public List<Observation> Variant = new List<Observation>();

            public HashSet<string> Lifted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int InstanceCount;
            public int OuterCount;

            public bool IsInstanceObservation(string key) => Lifted.Contains(key);
        }

        internal const string OuterPrefix = "dis|";

        internal static ModuleObservations ExtractModuleObservations(ModuleSplit split, InferenceOptions options, List<string> notes)
        {
            var variants = split.Outer;
            var instancesOf = variants.ToDictionary(v => v.Name, v => split.InstancesOf(v.Name).Select(i => i.Name).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var mo = new ModuleObservations();

            // Kopyaların gözlemleri: her kopya, modülün master'daki hâliyle yapısına göre eşleştirilir.
            mo.InstanceExtractor = new ObservationExtractor(options);
            if (options.MatchByStructure != false)
            {
                var unmatched = mo.InstanceExtractor.UseStructure(split.InstanceReference, split.Instances, split.DocumentMaps);
                if (unmatched > 0) notes.Add($"Modül kopyalarında {unmatched} bileşen master'la eşlenemedi.");
            }
            var instObs = mo.InstanceExtractor.Extract(split.Instances);
            if (mo.InstanceExtractor.UnstableFeatureFamilies.Count > 0)
                notes.Add("Şu özelliklerin numaraları kopyalar arasında kayıyor; adlarına güvenilemediği için bastırma kuralı çıkarılmadı: " +
                          string.Join(", ", mo.InstanceExtractor.UnstableFeatureFamilies) + ".");

            // Dış kısmın (hat montajı, kopyaların dışındaki parçalar) gözlemleri.
            mo.OuterExtractor = new ObservationExtractor(options);
            var outerReference = split.OuterReference ?? variants[0].Snapshot;
            mo.OuterExtractor.UseStructure(outerReference, variants, split.OuterReference != null ? split.DocumentMaps : null);
            var outerObs = mo.OuterExtractor.Extract(variants);
            var skipped = mo.InstanceExtractor.SkippedCalculated.Concat(mo.OuterExtractor.SkippedCalculated).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (skipped.Count > 0)
                notes.Add($"SolidWorks'ün hesapladığı özellikler kural dışı bırakıldı: {string.Join(", ", skipped)}.");

            // Bir varyantın kopyaları arasında değişen = satır; hep aynı = genel (varyant düzeyine taşınır).
            var lifted = new List<Observation>();
            foreach (var o in instObs)
            {
                bool varies = instancesOf.Values.Any(names => names.Where(o.Values.ContainsKey).Select(n => o.Values[n]).Distinct().Count() > 1);
                if (varies)
                {
                    mo.Row.Add(o);
                    continue;
                }
                var l = new Observation(o.Key, o.Target, o.Label);
                foreach (var v in variants)
                {
                    var first = instancesOf[v.Name].FirstOrDefault(o.Values.ContainsKey);
                    if (first != null) l.Values[v.Name] = o.Values[first];
                }
                lifted.Add(l);
            }
            mo.Lifted = new HashSet<string>(lifted.Select(o => o.Key), StringComparer.OrdinalIgnoreCase);
            // Dış gözlemlerin anahtarları kopya anahtarlarıyla çakışmasın.
            mo.Variant = lifted.Concat(outerObs.Select(o => Rekey(o, OuterPrefix))).ToList();
            mo.Instance = instObs;
            mo.Outer = outerObs;
            mo.InstanceCount = instObs.Count;
            mo.OuterCount = outerObs.Count;
            return mo;
        }

        internal static RuleTarget OuterTarget(Observation o, ModuleSplit split, ObservationExtractor outerExtractor) =>
            MapToMaster(o.Target, split.OuterReference, outerExtractor);

        private static Observation Rekey(Observation o, string prefix)
        {
            var copy = new Observation(prefix + o.Key, o.Target, o.Label);
            foreach (var kv in o.Values) copy.Values[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>
        /// Öneriler: genel girdilerle ilgili belirsizlikler yeni varyantlar, satır girdileriyle ilgili olanlar bu varyantlara
        /// eklenen satırlar olur (bir varyant birden çok satır değerini aynı anda dener). Tablo özetine bağlı belirsizlikler
        /// (ör. ilk satır) doğrudan o satıra yazılır.
        /// </summary>
        private static List<SuggestedVariant> PlanWithRows(InferenceReport report, TableDefinition tableDef, List<InputColumn> globalColumns,
            List<InputColumn> rowColumns, List<InputColumn> aggregates, List<ProbeNeed> needs, List<ProbeNeed> rowNeeds)
        {
            var globalNames = new HashSet<string>(globalColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var rowNames = new HashSet<string>(rowColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var firstOf = rowColumns.ToDictionary(c => TableSymbols.First(c.Name), c => c.Name, StringComparer.OrdinalIgnoreCase);
            var countName = TableSymbols.Count(tableDef);

            var globalNeeds = new List<ProbeNeed>();
            var rowProbes = new List<(Dictionary<string, Value> row, string reason)>();
            var firstRowProbes = new List<(Dictionary<string, Value> globals, Dictionary<string, Value> row, int? count, string reason)>();
            foreach (var need in needs.Concat(rowNeeds))
            {
                if (need.IsManual)
                {
                    globalNeeds.Add(need);
                    continue;
                }
                foreach (var a in need.Assignments())
                {
                    if (a.Keys.All(globalNames.Contains))
                    {
                        globalNeeds.Add(new ProbeNeed { Priority = need.Priority, Input = need.Input, Combos = { a }, Reason = need.Reason });
                        continue;
                    }
                    if (a.Keys.All(rowNames.Contains))
                    {
                        rowProbes.Add((a, need.Reason));
                        continue;
                    }
                    var globals = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                    var row = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                    int? count = null;
                    bool ok = true;
                    foreach (var kv in a)
                    {
                        if (globalNames.Contains(kv.Key)) globals[kv.Key] = kv.Value;
                        else if (firstOf.TryGetValue(kv.Key, out var col)) row[col] = kv.Value;
                        else if (string.Equals(kv.Key, countName, StringComparison.OrdinalIgnoreCase)) count = (int)Math.Round(kv.Value.AsNumber());
                        else ok = false; // toplam/en büyük gibi özetler doğrudan ayarlanamaz
                    }
                    if (ok) firstRowProbes.Add((globals, row, count, need.Reason));
                    else report.Notes.Add($"Öneriye dönüştürülemedi (tablo özetine bağlı): {need.Reason}");
                }
            }

            var notes = report.Notes;
            var variants = VariantPlanner.Plan(report.Inputs, globalColumns, globalNeeds, notes);
            var coverage = new List<ProbeNeed>();
            VariantPlanner.AddCoverageNeeds(rowColumns, coverage);
            foreach (var need in coverage.Where(n => !n.IsManual))
                foreach (var a in need.Assignments())
                    rowProbes.Add((a, need.Reason));

            int maxRows = Math.Max(1, tableDef.MaxRows ?? 1);
            int minRows = Math.Max(1, tableDef.MinRows ?? 1);
            SuggestedVariant NewVariant()
            {
                var v = new SuggestedVariant { Name = $"ONERI{variants.Count + 1:00}" };
                for (int ci = 0; ci < globalColumns.Count; ci++) v.Inputs[globalColumns[ci].Name] = VariantPlanner.Fill(globalColumns[ci], ci, variants.Count);
                variants.Add(v);
                return v;
            }

            foreach (var (globals, row, count, reason) in firstRowProbes)
            {
                var v = variants.FirstOrDefault(x => x.Rows.Count == 0 &&
                                                     globals.All(kv => x.Inputs.TryGetValue(kv.Key, out var cur) && Value.LooseEquals(cur, kv.Value)))
                        ?? NewVariant();
                foreach (var kv in globals) v.Inputs[kv.Key] = kv.Value;
                if (row.Count > 0) v.Rows.Insert(0, row);
                if (count.HasValue) v.RowCount = Math.Max(1, count.Value);
                v.Reasons.Add(reason);
            }
            foreach (var (row, reason) in rowProbes)
            {
                var v = variants.FirstOrDefault(x => x.Rows.Count < (x.RowCount ?? maxRows)) ?? NewVariant();
                v.Rows.Add(row);
                var text = $"{string.Join(", ", row.Select(kv => $"{kv.Key} = {kv.Value.AsText()}"))} (bir satırda): {reason}";
                if (!v.Reasons.Contains(text)) v.Reasons.Add(text);
            }

            // Satırları tamamla: sütunlar Halton dizisiyle, satır sayısı en az görülen kadar.
            for (int vi = 0; vi < variants.Count; vi++)
            {
                var v = variants[vi];
                int target = Math.Max(v.RowCount ?? minRows, Math.Max(minRows, v.Rows.Count));
                while (v.Rows.Count < target) v.Rows.Add(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase));
                for (int ri = 0; ri < v.Rows.Count; ri++)
                    for (int ci = 0; ci < rowColumns.Count; ci++)
                        if (!v.Rows[ri].ContainsKey(rowColumns[ci].Name))
                            v.Rows[ri][rowColumns[ci].Name] = VariantPlanner.Fill(rowColumns[ci], ci, vi * maxRows + ri);
                v.RowCount = v.Rows.Count;
            }
            report.SuggestionTable = tableDef.Name;
            return variants;
        }
    }
}
