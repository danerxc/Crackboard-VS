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
        private Timer _typingTimer;
        private DateTime _lastHeartbeatTime;
        private string _sessionKey;
        private RunningDocumentTable _runningDocTable;
        private uint _rdtCookie;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = (DTE)await GetServiceAsync(typeof(DTE));
            if (_dte == null) return;

            // Load session key from the options page settings
            _sessionKey = GetSessionKey();

            if (string.IsNullOrEmpty(_sessionKey))
            {
                Console.WriteLine("Session key not set. Please configure it in the options.");
            }

            Console.WriteLine($"Language of active document is: {_dte.ActiveDocument.Language}");

            _runningDocTable = new RunningDocumentTable(this);
            _rdtCookie = _runningDocTable.Advise(this);

            var textEditorEvents = _dte.Events.TextEditorEvents;
            textEditorEvents.LineChanged += OnLineChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (_rdtCookie != 0 && _runningDocTable != null)
            {
                _runningDocTable.Unadvise(_rdtCookie);
                _rdtCookie = 0;
            }

            base.Dispose(disposing);
        }

        // Event handler for document save
        public int OnBeforeSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
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
            if (_typingTimer == null)
            {
                _lastHeartbeatTime = DateTime.UtcNow;

                _typingTimer = new Timer(_ =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                            // Check if 2 minutes have passed since the last heartbeat
                            
                            if (DateTime.UtcNow.Subtract(_lastHeartbeatTime).TotalMinutes >= 2)
                            {
                                string vsLanguage = _dte.ActiveDocument?.Language;
                                string mappedLanguage = ConvertLanguageMapping(vsLanguage);
                                if (!string.IsNullOrEmpty(mappedLanguage))
                                {
                                    await SendHeartbeatAsync(mappedLanguage);
                                    _lastHeartbeatTime = DateTime.UtcNow;  // Update the last heartbeat time
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Handle exceptions safely
                            Console.WriteLine($"Error in Timer callback: {ex.Message}");
                        }
                    });
                }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));  // Check every 2 minutes
            }
        }

        // Finds currently open document by cookie
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

        // Sends a heartbeat to the Crackboard endpoint
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
                        _lastHeartbeatTime = DateTime.Now;
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

        // Gets the session key from the options page settings
        private string GetSessionKey()
        {
            OptionsPageGrid optionsPage = (OptionsPageGrid)GetDialogPage(typeof(OptionsPageGrid));
            return optionsPage.GetSessionKey();
        }

        // Dictionary to map common Visual Studio language names to Crackboard (VS Code) language names
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

        // Converts Visual Studio language names to Crackboard (VS Code) language names
        // Returns current language as lowercase if no mapping is found
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
