using Encode.Models;

namespace Encode.Services
{
    /// <summary>
    /// Manages loading and saving of application configuration (config.json).
    /// </summary>
    public class ConfigService
    {
        private readonly string _path;
        public Config Current { get; private set; }

        public ConfigService(string configPath)
        {
            _path = configPath;
            Current = Config.Load(_path);
        }

        /// <summary>
        /// Persist the current configuration back to file.
        /// </summary>
        public void Save()
        {
            Current.Save(_path);
        }
    }
}
