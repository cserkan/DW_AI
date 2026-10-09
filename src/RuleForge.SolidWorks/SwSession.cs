using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RuleForge.SolidWorks
{
    /// <summary>
    /// Çalışan SolidWorks'e bağlanır (yoksa başlatır). Bu oturumda açılan belgeleri takip eder,
    /// böylece kullanıcının zaten açık tuttuğu belgeler kapatılmaz.
    /// </summary>
    public sealed class SwSession : IDisposable
    {
        private readonly HashSet<string> _openedByUs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _startedByUs;

        private SwSession(ISldWorks app, bool startedByUs)
        {
            App = app;
            _startedByUs = startedByUs;
        }

        public ISldWorks App { get; }

        /// <summary>Ör. "34.1.0". İlk sayı + 1992 = sürüm yılı (23 = 2015, 32 = 2024, 34 = 2026).</summary>
        public string Revision => App.RevisionNumber();

        public string VersionLabel
        {
            get
            {
                var rev = Revision;
                var dot = rev.IndexOf('.');
                return int.TryParse(dot > 0 ? rev.Substring(0, dot) : rev, out var major)
                    ? $"SOLIDWORKS {major + 1992} ({rev})"
                    : rev;
            }
        }

        public static SwSession Connect(bool visible = false, bool forceNewInstance = false)
        {
            if (!forceNewInstance)
            {
                try
                {
                    if (Marshal.GetActiveObject("SldWorks.Application") is ISldWorks running)
                        return new SwSession(running, startedByUs: false);
                }
                catch (COMException)
                {
                    // Çalışan örnek yok, yenisini başlat.
                }
            }

            var type = Type.GetTypeFromProgID("SldWorks.Application")
                       ?? throw new InvalidOperationException("SolidWorks kurulu değil (SldWorks.Application COM kaydı bulunamadı).");
            var app = (ISldWorks)Activator.CreateInstance(type);
            app.Visible = visible;
            return new SwSession(app, startedByUs: true);
        }

        /// <summary>Belgeyi açar; zaten açıksa açık olanı döndürür.</summary>
        public ModelDoc2 Open(string path, bool readOnly = false)
        {
            path = Path.GetFullPath(path);
            if (App.GetOpenDocumentByName(path) is ModelDoc2 existing) return existing;
            if (!File.Exists(path)) throw new FileNotFoundException("Dosya bulunamadı: " + path, path);

            int errors = 0, warnings = 0;
            var options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
            if (readOnly) options |= (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;
            var doc = App.OpenDoc6(path, (int)DocumentTypeOf(path), options, "", ref errors, ref warnings);
            if (doc == null)
                throw new InvalidOperationException($"Açılamadı: {path} (hata kodu {errors}, swFileLoadError_e)");
            _openedByUs.Add(path);
            return doc;
        }

        /// <summary>Sadece bu oturumun açtığı belgeyi kapatır.</summary>
        public void Close(string path)
        {
            path = Path.GetFullPath(path);
            if (_openedByUs.Remove(path)) App.CloseDoc(path);
        }

        /// <summary>Belge kullanıcı tarafından açık olsa bile kapatır (aynı adlı kopya açmadan önce gerekir).</summary>
        public void ForceClose(string path)
        {
            path = Path.GetFullPath(path);
            _openedByUs.Remove(path);
            if (App.GetOpenDocumentByName(path) != null) App.CloseDoc(path);
        }

        public static swDocumentTypes_e DocumentTypeOf(string path)
        {
            switch (Path.GetExtension(path).ToUpperInvariant())
            {
                case ".SLDASM": return swDocumentTypes_e.swDocASSEMBLY;
                case ".SLDPRT": return swDocumentTypes_e.swDocPART;
                case ".SLDDRW": return swDocumentTypes_e.swDocDRAWING;
                default: throw new ArgumentException("Desteklenmeyen dosya türü: " + path);
            }
        }

        public void Dispose()
        {
            foreach (var path in new List<string>(_openedByUs)) Close(path);
            if (_startedByUs)
            {
                try { App.ExitApp(); } catch (COMException) { }
            }
        }
    }
}
