namespace SpookyFisher
{
    // CLR hosting requires this exact public static int(string) signature.
    public static class Entry
    {
        public static int Start(string eventPrefix)
        {
            return Runtime.Start(eventPrefix);
        }
    }
}
