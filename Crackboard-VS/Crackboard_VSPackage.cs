using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crackboard_VS.options;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;

namespace Crackboard_VS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    [ProvideOptionPage(typeof(OptionsPageGrid), "Crackboard", "General", 0, 0, true)]
    public sealed class CrackboardPackage : AsyncPackage, IVsRunningDocTableEvents3
    {
        public const string PackageGuidString = "2817f103-f558-4681-b1be-19759908d9dd";
        private static readonly string Endpoint = "http://crackboard.dev/heartbeat";
        private DTE _dte;
        private DateTime _lastHeartbeatTime;
        private DateTime _lastActivityTime;
        private string _sessionKey;
        private RunningDocumentTable _runningDocTable;
        private uint _rdtCookie;
        private Timer _idleTimer;
        private const int HEARTBEAT_INTERVAL = 2 * 60 * 1000;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = (DTE)await GetServiceAsync(typeof(DTE));
            if (_dte == null) return;

            _sessionKey = GetSessionKey();

            if (string.IsNullOrEmpty(_sessionKey))
            {
                Console.WriteLine("Session key not set. Please configure it in the options.");
            }

            _runningDocTable = new RunningDocumentTable(this);
            _rdtCookie = _runningDocTable.Advise(this);

            var textEditorEvents = _dte.Events.TextEditorEvents;
            textEditorEvents.LineChanged += OnLineChanged;

            _lastActivityTime = DateTime.UtcNow;
            _lastHeartbeatTime = DateTime.UtcNow;
            _idleTimer = new Timer(CheckIdleAndSendHeartbeat, null, HEARTBEAT_INTERVAL, HEARTBEAT_INTERVAL);
        }

        protected override void Dispose(bool disposing)
        {
            if (_rdtCookie != 0 && _runningDocTable != null)
            {
                _runningDocTable.Unadvise(_rdtCookie);
                _rdtCookie = 0;
            }

            _idleTimer?.Dispose();

            base.Dispose(disposing);
        }

        public int OnBeforeSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _lastActivityTime = DateTime.UtcNow;
            var document = FindDocumentByCookie(docCookie);
            if (document != null)
            {
                string vsLanguage = document.Language;
                string mappedLanguage = ConvertLanguageMapping(vsLanguage);

                _ = SendHeartbeatAsync(mappedLanguage).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Console.WriteLine($"Error sending heartbeat: {t.Exception?.Message}");
                    }
                }, TaskScheduler.Current);
            }
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

        private void OnLineChanged(TextPoint startPoint, TextPoint endPoint, int hint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _lastActivityTime = DateTime.UtcNow;

            string vsLanguage = _dte.ActiveDocument?.Language;
            string mappedLanguage = ConvertLanguageMapping(vsLanguage);

            if (!string.IsNullOrEmpty(mappedLanguage))
            {
                var now = DateTime.UtcNow;
                var timeSinceLastHeartbeat = now - _lastHeartbeatTime;

                if (timeSinceLastHeartbeat.TotalMilliseconds >= HEARTBEAT_INTERVAL)
                {
                    _ = SendHeartbeatAsync(mappedLanguage);
                }
            }
        }

        private void CheckIdleAndSendHeartbeat(object state)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var now = DateTime.UtcNow;
                var timeSinceLastActivity = now - _lastActivityTime;
                var timeSinceLastHeartbeat = now - _lastHeartbeatTime;

                if (timeSinceLastActivity.TotalMilliseconds < HEARTBEAT_INTERVAL &&
                    timeSinceLastHeartbeat.TotalMilliseconds >= HEARTBEAT_INTERVAL)
                {
                    string vsLanguage = _dte.ActiveDocument?.Language;
                    string mappedLanguage = ConvertLanguageMapping(vsLanguage);
                    if (!string.IsNullOrEmpty(mappedLanguage))
                    {
                        await SendHeartbeatAsync(mappedLanguage);
                    }
                }
            });
        }

        private Document FindDocumentByCookie(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var documentInfo = _runningDocTable.GetDocumentInfo(docCookie);
            return _dte.Documents.Cast<Document>().FirstOrDefault(doc =>
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                return doc.FullName == documentInfo.Moniker;
            });
        }

        private async Task SendHeartbeatAsync(string language)
        {
            if (string.IsNullOrEmpty(_sessionKey))
            {
                Console.WriteLine("Session key is not set, cannot send heartbeat.");
                return;
            }

            try
            {
                using (var client = new HttpClient())
                {
                    var payload = new
                    {
                        timestamp = DateTime.UtcNow.ToString("o"),
                        session_key = _sessionKey,
                        language_name = language
                    };

                    var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(Endpoint, content);
                    if (response.IsSuccessStatusCode)
                    {
                        _lastHeartbeatTime = DateTime.UtcNow;
                        Console.WriteLine("Heartbeat sent successfully.");
                    }
                    else
                    {
                        Console.WriteLine("Failed to send heartbeat: " + response.ReasonPhrase);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error sending heartbeat: " + ex.Message);
            }
        }

        private string GetSessionKey()
        {
            OptionsPageGrid optionsPage = (OptionsPageGrid)GetDialogPage(typeof(OptionsPageGrid));
            return optionsPage.SessionKey;
        }

        private static readonly Dictionary<string, string> LanguageMapDictionary = new Dictionary<string, string>
        {
            { "CSharp", "csharp" },
            { "VisualBasic", "vb" },
            { "HTML", "html" },
            { "CSS", "css" },
            { "JavaScript", "javascript" },
            { "TypeScript", "typescript" },
            { "XML", "xml" },
            { "SQL", "sql" },
            { "Python", "python" },
            { "FSharp", "fsharp" },
            { "C/C++", "cpp" },
            { "Java", "java" },
        };

        private string ConvertLanguageMapping(string vsLanguage)
        {
            if (LanguageMapDictionary.TryGetValue(vsLanguage, out string mappedLanguage))
            {
                return mappedLanguage;
            }
            return vsLanguage.ToLower();
        }
    }
}