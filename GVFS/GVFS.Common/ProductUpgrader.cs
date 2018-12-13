using GVFS.Common.Tracing;
using System;

namespace GVFS.Common
{
    public partial class ProductUpgrader
    {
        public static IProductUpgrader CreateUpgrader(ITracer tracer, out string error)
        {
            IProductUpgrader upgrader;

            bool isEnabled;
            bool isConfigured;
            if (NuGetUpgrader.TryCreateNuGetUpgrader(tracer, out isEnabled, out isConfigured, out upgrader, out error))
            {
                return upgrader;
            }

            if (isEnabled && !isConfigured)
            {
                // Configuration error
                return null;
            }

            error = string.Empty;
            
            if ((upgrader = GitHubUpgrader.Create(tracer, out isEnabled, out isConfigured)) != null)
            {
                return upgrader;
            }

            if (isEnabled && !isConfigured)
            {
                // Configuration error
                return null;
            }

            if (tracer != null)
            {
                tracer.RelatedError($"{nameof(CreateUpgrader)}: Could not create upgrader. {error}");
            }

            error = GVFSConstants.UpgradeVerbMessages.InvalidRingConsoleAlert + Environment.NewLine + Environment.NewLine + "Error: " + error;
            return null;
        }
    }
}
