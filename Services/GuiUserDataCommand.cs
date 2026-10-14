namespace MediaFlux.Services;

internal static class GuiUserDataCommand
{
    internal static bool IsRequested(string[] args) =>
        args.Any(argument => argument.StartsWith("--user-data", StringComparison.OrdinalIgnoreCase));

    internal static bool TryResolve(string[] args, MediaFluxStoragePathService normalStorage,
        out MediaFluxStoragePathService? isolatedStorage, out string error)
    {
        isolatedStorage = null;
        error = string.Empty;
        string? root = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--user-data", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!args[i].Equals("--user-data", StringComparison.OrdinalIgnoreCase))
            {
                error = "GUI UserData override syntax is '--user-data <absolute-directory>'; equals-form and unknown UserData options are not supported.";
                return false;
            }
            if (root is not null || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "GUI --user-data must appear once and be followed by an absolute directory path.";
                return false;
            }
            root = args[++i];
        }

        if (root is null)
            return true;

        try
        {
            isolatedStorage = MediaFluxStoragePathService.ForIsolatedGuiRoot(root, normalStorage.Root);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            error = "The isolated GUI UserData path is invalid or unsafe: " + ex.Message;
            return false;
        }
    }
}
