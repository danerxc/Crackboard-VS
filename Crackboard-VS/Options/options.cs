using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace Crackboard_VS.options
{
    public class OptionsPageGrid : DialogPage
    {
        private string sessionKey = string.Empty;

        public string GetSessionKey()
        { return sessionKey; }

        public void SetSessionKey(string value)
        { sessionKey = value; }
    }
}
