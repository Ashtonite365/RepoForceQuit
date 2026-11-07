namespace RepoForceQuit
{
    internal static class QuitButtons
    {
        public static bool IsInGameQuitMethod(string typeName, string methodName)
        {
            if (methodName != "ButtonEventQuit")
                return false;
            return typeName == "MenuPageMain" || typeName == "MenuPageEsc";
        }
    }
}
