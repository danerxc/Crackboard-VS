using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace Crackboard_VS.options
{
    public class OptionsPageGrid : DialogPage
    {
        private string sessionKey = string.Empty;

        [Category("Crackboard")]
        [DisplayName("Session Key")]
        [Description("The session key used for authentication.")]
        public string SessionKey
        {
            get { return sessionKey; }
            set { sessionKey = value; }
        }
    }
}
