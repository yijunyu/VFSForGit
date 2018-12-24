using CommandLine;
using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Http;
using GVFS.Common.NamedPipes;
using GVFS.Common.Tracing;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace GVFS.CommandLine
{
    public abstract class GVFSVerb
    {
        protected const string StartServiceInstructions = "Run 'sc start GVFS.Service' from an elevated command prompt to ensure it is running.";

        private readonly bool validateOriginURL;

        public GVFSVerb(bool validateOrigin = true)
        {
            this.Output = Console.Out;
            this.ReturnCode = ReturnCode.Success;
            this.validateOriginURL = validateOrigin;
            this.ServiceName = GVFSConstants.Service.ServiceName;
            this.StartedByService = false;
            this.Unattended = GVFSEnlistment.IsUnattended(tracer: null);

            this.InitializeDefaultParameterValues();
        }

        public abstract string EnlistmentRootPathParameter { get; set; }

        [Option(
            GVFSConstants.VerbParameters.InternalUseOnly,
            Required = false,
            HelpText = "This parameter is reserved for internal use.")]
        public string InternalParameters
        {
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    try
                    {
                        InternalVerbParameters mountInternal = InternalVerbParameters.FromJson(value);
                        if (!string.IsNullOrEmpty(mountInternal.ServiceName))
                        {
                            this.ServiceName = mountInternal.ServiceName;
                        }

                        if (!string.IsNullOrEmpty(mountInternal.MaintenanceJob))
                        {
                            this.MaintenanceJob = mountInternal.MaintenanceJob;
                        }

                        if (!string.IsNullOrEmpty(mountInternal.PackfileMaintenanceBatchSize))
                        {
                            this.PackfileMaintenanceBatchSize = mountInternal.PackfileMaintenanceBatchSize;
                        }

                        this.StartedByService = mountInternal.StartedByService;
                    }
                    catch (JsonReaderException e)
                    {
                        this.ReportErrorAndExit("Failed to parse InternalParameters: {0}.\n {1}", value, e);
                    }                    
                }
            }
        }

        public string ServiceName { get; set; }

        public string MaintenanceJob { get; set; }

        public string PackfileMaintenanceBatchSize { get; set; }

        public bool StartedByService { get; set; }

        public bool Unattended { get; private set; }

        public string ServicePipeName
        {
            get
            {
                return this.ServiceName + ".Pipe";
            }
        }

        public TextWriter Output { get; set; }

        public ReturnCode ReturnCode { get; private set; }

        protected abstract string VerbName { get; }

        public static bool TrySetRequiredGitConfigSettings(Enlistment enlistment)
        {
            string expectedHooksPath = Path.Combine(enlistment.WorkingDirectoryRoot, GVFSConstants.DotGit.Hooks.Root);
            expectedHooksPath = Paths.ConvertPathToGitFormat(expectedHooksPath);

            string gitStatusCachePath = null;
            if (!GVFSEnlistment.IsUnattended(tracer: null) && GVFSPlatform.Instance.IsGitStatusCacheSupported())
            {
                gitStatusCachePath = Path.Combine(
                    enlistment.EnlistmentRoot,
                    GVFSConstants.DotGVFS.Root,
                    GVFSConstants.DotGVFS.GitStatusCache.CachePath);

                gitStatusCachePath = Paths.ConvertPathToGitFormat(gitStatusCachePath);
            }

            // These settings are required for normal GVFS functionality.
            // They will override any existing local configuration values.
            //
            // IMPORTANT! These must parallel the settings in ControlGitRepo:Initialize
            //
            Dictionary<string, string> requiredSettings = new Dictionary<string, string>
            {
                { "am.keepcr", "true" },
                { "checkout.optimizenewbranch", "true" },
                { "core.autocrlf", "false" },
                { "core.commitGraph", "true" },
                { "core.fscache", "true" },
                { "core.gvfs", "true" },
                { "core.multiPackIndex", "true" },
                { "core.preloadIndex", "true" },
                { "core.safecrlf", "false" },
                { "core.untrackedCache", "false" },
                { "core.repositoryformatversion", "0" },
                { "core.filemode", GVFSPlatform.Instance.FileSystem.SupportsFileMode ? "true" : "false" },
                { "core.bare", "false" },
                { "core.logallrefupdates", "true" },
                { GitConfigSetting.CoreVirtualizeObjectsName, "true" },
                { GitConfigSetting.CoreVirtualFileSystemName, Paths.ConvertPathToGitFormat(GVFSConstants.DotGit.Hooks.VirtualFileSystemPath) },
                { "core.hookspath", expectedHooksPath },
                { GitConfigSetting.CredentialUseHttpPath, "true" },
                { "credential.validate", "false" },
                { "diff.autoRefreshIndex", "false" },
                { "gc.auto", "0" },
                { "gui.gcwarning", "false" },
                { "index.threads", "true" },
                { "index.version", "4" },
                { "merge.stat", "false" },
                { "merge.renames", "false" },
                { "pack.useBitmaps", "false" },
                { "pack.useSparse", "true" },
                { "receive.autogc", "false" },
                { "reset.quiet", "true" },
                { "status.deserializePath", gitStatusCachePath },
            };

            if (!TrySetConfig(enlistment, requiredSettings, isRequired: true))
            {
                return false;
            }

            return true;
        }

        public static bool TrySetOptionalGitConfigSettings(Enlistment enlistment)
        {
            // These settings are optional, because they impact performance but not functionality of GVFS.
            // These settings should only be set by the clone or repair verbs, so that they do not
            // overwrite the values set by the user in their local config.
            Dictionary<string, string> optionalSettings = new Dictionary<string, string>
            {
                { "status.aheadbehind", "false" },
            };

            if (!TrySetConfig(enlistment, optionalSettings, isRequired: false))
            {
                return false;
            }

            return true;
        }

        public abstract void Execute();

        public virtual void InitializeDefaultParameterValues()
        {
        }

        protected ReturnCode Execute<TVerb>(
            string enlistmentRootPath,
            Action<TVerb> configureVerb = null)
            where TVerb : GVFSVerb, new()
        {
            TVerb verb = new TVerb();
            verb.EnlistmentRootPathParameter = enlistmentRootPath;
            verb.ServiceName = this.ServiceName;
            verb.Unattended = this.Unattended;

            if (configureVerb != null)
            {
                configureVerb(verb);
            }

            try
            {
                verb.Execute();
            }
            catch (VerbAbortedException)
            {
            }

            return verb.ReturnCode;
        }

        protected ReturnCode Execute<TVerb>(
            GVFSEnlistment enlistment,
            Action<TVerb> configureVerb = null)
            where TVerb : GVFSVerb.ForExistingEnlistment, new()
        {
            TVerb verb = new TVerb();
            verb.EnlistmentRootPathParameter = enlistment.EnlistmentRoot;
            verb.ServiceName = this.ServiceName;
            verb.Unattended = this.Unattended;

            if (configureVerb != null)
            {
                configureVerb(verb);
            }

            try
            {
                verb.Execute(enlistment.Authentication);
            }
            catch (VerbAbortedException)
            {
            }

            return verb.ReturnCode;
        }

        protected bool ShowStatusWhileRunning(
            Func<bool> action,
            string message,
            string gvfsLogEnlistmentRoot)
        {
            return ConsoleHelper.ShowStatusWhileRunning(
                action,
                message,
                this.Output,
                showSpinner: !this.Unattended && this.Output == Console.Out && !GVFSPlatform.Instance.IsConsoleOutputRedirectedToFile(),
                gvfsLogEnlistmentRoot: gvfsLogEnlistmentRoot,
                initialDelayMs: 0);
        }

        protected bool ShowStatusWhileRunning(
            Func<bool> action,
            string message,
            bool suppressGvfsLogMessage = false)
        {
            string gvfsLogEnlistmentRoot = null;
            if (!suppressGvfsLogMessage)
            {
                string errorMessage;
                GVFSPlatform.Instance.TryGetGVFSEnlistmentRoot(this.EnlistmentRootPathParameter, out gvfsLogEnlistmentRoot, out errorMessage);
            }

            return this.ShowStatusWhileRunning(action, message, gvfsLogEnlistmentRoot);
        }

        protected bool TryAuthenticate(ITracer tracer, GVFSEnlistment enlistment, out string authErrorMessage)
        {
            string authError = null;

            bool result = this.ShowStatusWhileRunning(
                () => enlistment.Authentication.TryInitialize(tracer, enlistment, out authError),
                "Authenticating",
                enlistment.EnlistmentRoot);

            authErrorMessage = authError;
            return result;
        }

        protected void ReportErrorAndExit(ITracer tracer, ReturnCode exitCode, string error, params object[] args)
        {
            if (!string.IsNullOrEmpty(error))
            {
                if (args == null || args.Length == 0)
                {
                    this.Output.WriteLine(error);
                    if (tracer != null && exitCode != ReturnCode.Success)
                    {
                        tracer.RelatedError(error);
                    }
                }
                else
                {
                    this.Output.WriteLine(error, args);
                    if (tracer != null && exitCode != ReturnCode.Success)
                    {
                        tracer.RelatedError(error, args);
                    }
                }
            }

            this.ReturnCode = exitCode;
            throw new VerbAbortedException(this);
        }

        protected void ReportErrorAndExit(string error, params object[] args)
        {
            this.ReportErrorAndExit(tracer: null, exitCode: ReturnCode.GenericError, error: error, args: args);
        }

        protected void ReportErrorAndExit(ITracer tracer, string error, params object[] args)
        {
            this.ReportErrorAndExit(tracer, ReturnCode.GenericError, error, args);
        }

        protected RetryConfig GetRetryConfig(ITracer tracer, GVFSEnlistment enlistment, TimeSpan? timeoutOverride = null)
        {
            RetryConfig retryConfig;
            string error;
            if (!RetryConfig.TryLoadFromGitConfig(tracer, enlistment, out retryConfig, out error))
            {
                this.ReportErrorAndExit(tracer, "Failed to determine GVFS timeout and max retries: " + error);
            }

            if (timeoutOverride.HasValue)
            {
                retryConfig.Timeout = timeoutOverride.Value;
            }

            return retryConfig;
        }

        protected ServerGVFSConfig QueryGVFSConfig(ITracer tracer, GVFSEnlistment enlistment, RetryConfig retryConfig)
        {
            ServerGVFSConfig serverGVFSConfig = null;
            if (!this.ShowStatusWhileRunning(
                () =>
                {
                    using (ConfigHttpRequestor configRequestor = new ConfigHttpRequestor(tracer, enlistment, retryConfig))
                    {
                        const bool LogErrors = true;
                        return configRequestor.TryQueryGVFSConfig(LogErrors, out serverGVFSConfig, out _);
                    }
                },
                "Querying remote for config",
                suppressGvfsLogMessage: true))
            {
                this.ReportErrorAndExit(tracer, "Unable to query /gvfs/config");
            }

            return serverGVFSConfig;
        }        

        protected void ValidateClientVersions(ITracer tracer, GVFSEnlistment enlistment, ServerGVFSConfig gvfsConfig, bool showWarnings)
        {
            this.CheckGitVersion(tracer, enlistment, out string gitVersion);
            enlistment.SetGitVersion(gitVersion);

            this.GetGVFSHooksPathAndCheckVersion(tracer, out string hooksVersion);
            enlistment.SetGVFSHooksVersion(hooksVersion);
            this.CheckFileSystemSupportsRequiredFeatures(tracer, enlistment);

            string errorMessage = null;
            bool errorIsFatal = false;
            if (!this.TryValidateGVFSVersion(enlistment, tracer, gvfsConfig, out errorMessage, out errorIsFatal))
            {
                if (errorIsFatal)
                {
                    this.ReportErrorAndExit(tracer, errorMessage);
                }
                else if (showWarnings)
                {
                    this.Output.WriteLine();
                    this.Output.WriteLine(errorMessage);
                    this.Output.WriteLine();
                }
            }
        }

        protected bool TryCreateAlternatesFile(PhysicalFileSystem fileSystem, GVFSEnlistment enlistment, out string errorMessage)
        {
            try
            {
                string alternatesFilePath = this.GetAlternatesPath(enlistment);
                string tempFilePath = alternatesFilePath + ".tmp";
                fileSystem.WriteAllText(tempFilePath, enlistment.GitObjectsRoot);
                fileSystem.MoveAndOverwriteFile(tempFilePath, alternatesFilePath);
            }
            catch (SecurityException e)
            {
                errorMessage = e.Message;
                return false;
            }
            catch (IOException e)
            {
                errorMessage = e.Message;
                return false;
            }

            errorMessage = null;
            return true;
        }

        protected string GetGVFSHooksPathAndCheckVersion(ITracer tracer, out string hooksVersion)
        {
            string error;
            string hooksPath;
            if (!GVFSPlatform.Instance.TryGetGVFSHooksPathAndVersion(out hooksPath, out hooksVersion, out error))
            {
                this.ReportErrorAndExit(tracer, error);
            }

            string gvfsVersion = ProcessHelper.GetCurrentProcessVersion();
            if (hooksVersion != gvfsVersion)
            {
                this.ReportErrorAndExit(tracer, "GVFS.Hooks version ({0}) does not match GVFS version ({1}).", hooksVersion, gvfsVersion);
            }

            return hooksPath;
        }

        protected void BlockEmptyCacheServerUrl(string userInput)
        {
            if (userInput == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(userInput))
            {
                this.ReportErrorAndExit(
@"You must specify a value for the cache server.
You can specify a URL, a name of a configured cache server, or the special names None or Default.");
            }
        }

        protected CacheServerInfo ResolveCacheServer(
            ITracer tracer,
            CacheServerInfo cacheServer,
            CacheServerResolver cacheServerResolver,
            ServerGVFSConfig serverGVFSConfig)
        {
            CacheServerInfo resolvedCacheServer = cacheServer;

            if (cacheServer.Url == null)
            {
                string cacheServerName = cacheServer.Name;
                string error = null;

                if (!cacheServerResolver.TryResolveUrlFromRemote(
                        cacheServerName,
                        serverGVFSConfig,
                        out resolvedCacheServer,
                        out error))
                {
                    this.ReportErrorAndExit(tracer, error);
                }
            }
            else if (cacheServer.Name.Equals(CacheServerInfo.ReservedNames.UserDefined))
            {
                resolvedCacheServer = cacheServerResolver.ResolveNameFromRemote(cacheServer.Url, serverGVFSConfig);
            }

            this.Output.WriteLine("Using cache server: " + resolvedCacheServer);
            return resolvedCacheServer;
        }

        protected void ValidatePathParameter(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    Path.GetFullPath(path);
                }
                catch (Exception e)
                {
                    this.ReportErrorAndExit("Invalid path: '{0}' ({1})", path, e.Message);
                }
            }
        }

        protected bool TryDownloadCommit(
            string commitId,
            GVFSEnlistment enlistment,
            GitObjectsHttpRequestor objectRequestor,
            GVFSGitObjects gitObjects,
            GitRepo repo,
            out string error,
            bool checkLocalObjectCache = true)
        {
            if (!checkLocalObjectCache || !repo.CommitAndRootTreeExists(commitId))
            {
                if (!gitObjects.TryDownloadCommit(commitId))
                {
                    error = "Could not download commit " + commitId + " from: " + Uri.EscapeUriString(objectRequestor.CacheServer.ObjectsEndpointUrl);
                    return false;
                }
            }

            error = null;
            return true;
        }

        protected bool TryDownloadRootGitAttributes(GVFSEnlistment enlistment, GVFSGitObjects gitObjects, GitRepo repo, out string error)
        {
            List<DiffTreeResult> rootEntries = new List<DiffTreeResult>();
            GitProcess git = new GitProcess(enlistment);
            GitProcess.Result result = git.LsTree(
                GVFSConstants.DotGit.HeadName,
                line => rootEntries.Add(DiffTreeResult.ParseFromLsTreeLine(line, repoRoot: string.Empty)),
                recursive: false);

            if (result.ExitCodeIsFailure)
            {
                error = "Error returned from ls-tree to find " + GVFSConstants.SpecialGitFiles.GitAttributes + " file: " + result.Errors;
                return false;
            }

            DiffTreeResult gitAttributes = rootEntries.FirstOrDefault(entry => entry.TargetPath.Equals(GVFSConstants.SpecialGitFiles.GitAttributes));
            if (gitAttributes == null)
            {
                error = "This branch does not contain a " + GVFSConstants.SpecialGitFiles.GitAttributes + " file in the root folder.  This file is required by GVFS clone";
                return false;
            }

            if (!repo.ObjectExists(gitAttributes.TargetSha))
            {
                if (gitObjects.TryDownloadAndSaveObject(gitAttributes.TargetSha, GVFSGitObjects.RequestSource.GVFSVerb) != GitObjects.DownloadAndSaveObjectResult.Success)
                {
                    error = "Could not download " + GVFSConstants.SpecialGitFiles.GitAttributes + " file";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Request that PrjFlt be enabled and attached to the volume of the enlistment root
        /// </summary>
        /// <param name="enlistmentRoot">Enlistment root.  If string.Empty, PrjFlt will be enabled but not attached to any volumes</param>
        /// <param name="errorMessage">Error meesage (in the case of failure)</param>
        /// <returns>True is successful and false otherwise</returns>
        protected bool TryEnableAndAttachPrjFltThroughService(string enlistmentRoot, out string errorMessage)
        {
            errorMessage = string.Empty;

            NamedPipeMessages.EnableAndAttachProjFSRequest request = new NamedPipeMessages.EnableAndAttachProjFSRequest();
            request.EnlistmentRoot = enlistmentRoot;

            using (NamedPipeClient client = new NamedPipeClient(this.ServicePipeName))
            {
                if (!client.Connect())
                {
                    errorMessage = "GVFS.Service is not responding. " + GVFSVerb.StartServiceInstructions;
                    return false;
                }

                try
                {
                    client.SendRequest(request.ToMessage());
                    NamedPipeMessages.Message response = client.ReadResponse();
                    if (response.Header == NamedPipeMessages.EnableAndAttachProjFSRequest.Response.Header)
                    {
                        NamedPipeMessages.EnableAndAttachProjFSRequest.Response message = NamedPipeMessages.EnableAndAttachProjFSRequest.Response.FromMessage(response);

                        if (!string.IsNullOrEmpty(message.ErrorMessage))
                        {
                            errorMessage = message.ErrorMessage;
                            return false;
                        }

                        if (message.State != NamedPipeMessages.CompletionState.Success)
                        {
                            errorMessage = $"Failed to attach ProjFS to volume.";
                            return false;
                        }
                        else
                        {
                            return true;
                        }
                    }
                    else
                    {
                        errorMessage = string.Format("GVFS.Service responded with unexpected message: {0}", response);
                        return false;
                    }
                }
                catch (BrokenPipeException e)
                {
                    errorMessage = "Unable to communicate with GVFS.Service: " + e.ToString();
                    return false;
                }
            }
        }

        protected void LogEnlistmentInfoAndSetConfigValues(ITracer tracer, GitProcess git, GVFSEnlistment enlistment)
        {
            string mountId = CreateMountId();
            EventMetadata metadata = new EventMetadata();
            metadata.Add(nameof(RepoMetadata.Instance.EnlistmentId), RepoMetadata.Instance.EnlistmentId);
            metadata.Add(nameof(mountId), mountId);
            metadata.Add("Enlistment", enlistment);
            metadata.Add("PhysicalDiskInfo", GVFSPlatform.Instance.GetPhysicalDiskInfo(enlistment.WorkingDirectoryRoot, sizeStatsOnly: false));
            tracer.RelatedEvent(EventLevel.Informational, "EnlistmentInfo", metadata, Keywords.Telemetry);

            GitProcess.Result configResult = git.SetInLocalConfig(GVFSConstants.GitConfig.EnlistmentId, RepoMetadata.Instance.EnlistmentId, replaceAll: true);
            if (configResult.ExitCodeIsFailure)
            {
                string error = "Could not update config with enlistment id, error: " + configResult.Errors;
                tracer.RelatedWarning(error);
            }

            configResult = git.SetInLocalConfig(GVFSConstants.GitConfig.MountId, mountId, replaceAll: true);
            if (configResult.ExitCodeIsFailure)
            {
                string error = "Could not update config with mount id, error: " + configResult.Errors;
                tracer.RelatedWarning(error);
            }
        }

        private static string CreateMountId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static bool TrySetConfig(Enlistment enlistment, Dictionary<string, string> configSettings, bool isRequired)
        {
            GitProcess git = new GitProcess(enlistment);

            Dictionary<string, GitConfigSetting> existingConfigSettings;

            // If the settings are required, then only check local config settings, because we don't want to depend on
            // global settings that can then change independent of this repo.
            if (!git.TryGetAllConfig(localOnly: isRequired, configSettings: out existingConfigSettings))
            {
                return false;
            }

            foreach (KeyValuePair<string, string> setting in configSettings)
            {
                GitConfigSetting existingSetting;
                if (setting.Value != null)
                {
                    if (!existingConfigSettings.TryGetValue(setting.Key, out existingSetting) ||
                        (isRequired && !existingSetting.HasValue(setting.Value)))
                    {
                        GitProcess.Result setConfigResult = git.SetInLocalConfig(setting.Key, setting.Value);
                        if (setConfigResult.ExitCodeIsFailure)
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    if (existingConfigSettings.TryGetValue(setting.Key, out existingSetting))
                    {
                        git.DeleteFromLocalConfig(setting.Key);
                    }
                }
            }

            return true;
        }

        private string GetAlternatesPath(GVFSEnlistment enlistment)
        {
            return Path.Combine(enlistment.WorkingDirectoryRoot, GVFSConstants.DotGit.Objects.Info.Alternates);
        }

        private void CheckFileSystemSupportsRequiredFeatures(ITracer tracer, Enlistment enlistment)
        {
            try
            {
                string warning;
                string error;
                if (!GVFSPlatform.Instance.KernelDriver.IsSupported(enlistment.EnlistmentRoot, out warning, out error))
                {
                    this.ReportErrorAndExit(tracer, $"Error: {error}");
                }
            }
            catch (VerbAbortedException)
            {
                // ReportErrorAndExit throws VerbAbortedException.  Catch and re-throw here so that GVFS does not report that
                // it failed to determine if file system supports required features
                throw;
            }
            catch (Exception e)
            {
                if (tracer != null)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Exception", e.ToString());
                    tracer.RelatedError(metadata, "Failed to determine if file system supports features required by GVFS");
                }

                this.ReportErrorAndExit(tracer, "Error: Failed to determine if file system supports features required by GVFS.");
            }
        }

        private void CheckGitVersion(ITracer tracer, GVFSEnlistment enlistment, out string version)
        {
            GitVersion gitVersion = null;
            if (string.IsNullOrEmpty(enlistment.GitBinPath) || !GitProcess.TryGetVersion(enlistment.GitBinPath, out gitVersion, out string _))
            {
                this.ReportErrorAndExit(tracer, "Error: Unable to retrieve the git version");
            }

            version = gitVersion.ToString();

            if (gitVersion.Platform != GVFSConstants.SupportedGitVersion.Platform)
            {
                this.ReportErrorAndExit(tracer, "Error: Invalid version of git {0}.  Must use gvfs version.", version);
            }

            if (ProcessHelper.IsDevelopmentVersion())
            {
                if (gitVersion.IsLessThan(GVFSConstants.SupportedGitVersion))
                {
                    this.ReportErrorAndExit(
                        tracer,
                        "Error: Installed git version {0} is less than the supported version of {1}.",
                        gitVersion,
                        GVFSConstants.SupportedGitVersion);
                }
                else if (!gitVersion.IsEqualTo(GVFSConstants.SupportedGitVersion))
                {
                    this.Output.WriteLine($"Warning: Installed git version {gitVersion} does not match supported version of {GVFSConstants.SupportedGitVersion}.");
                }
            }
            else
            {
                if (!gitVersion.IsEqualTo(GVFSConstants.SupportedGitVersion))
                {
                    this.ReportErrorAndExit(
                        tracer,
                        "Error: Installed git version {0} does not match supported version of {1}.",
                        gitVersion,
                        GVFSConstants.SupportedGitVersion);
                }
            }
        }

        private bool TryValidateGVFSVersion(GVFSEnlistment enlistment, ITracer tracer, ServerGVFSConfig config, out string errorMessage, out bool errorIsFatal)
        {
            errorMessage = null;
            errorIsFatal = false;

            using (ITracer activity = tracer.StartActivity("ValidateGVFSVersion", EventLevel.Informational))
            {
                Version currentVersion = new Version(ProcessHelper.GetCurrentProcessVersion());

                IEnumerable<ServerGVFSConfig.VersionRange> allowedGvfsClientVersions =
                    config != null
                    ? config.AllowedGVFSClientVersions
                    : null;

                if (allowedGvfsClientVersions == null || !allowedGvfsClientVersions.Any())
                {
                    errorMessage = "WARNING: Unable to validate your GVFS version" + Environment.NewLine;
                    if (config == null)
                    {
                        errorMessage += "Could not query valid GVFS versions from: " + Uri.EscapeUriString(enlistment.RepoUrl);
                    }
                    else
                    {
                        errorMessage += "Server not configured to provide supported GVFS versions";
                    }

                    EventMetadata metadata = new EventMetadata();
                    tracer.RelatedError(metadata, errorMessage, Keywords.Network);

                    return false;
                }

                foreach (ServerGVFSConfig.VersionRange versionRange in config.AllowedGVFSClientVersions)
                {
                    if (currentVersion >= versionRange.Min &&
                        (versionRange.Max == null || currentVersion <= versionRange.Max))
                    {
                        activity.RelatedEvent(
                            EventLevel.Informational,
                            "GVFSVersionValidated",
                            new EventMetadata
                            {
                                { "SupportedVersionRange", versionRange },
                            });

                        enlistment.SetGVFSVersion(currentVersion.ToString());
                        return true;
                    }
                }

                activity.RelatedError("GVFS version {0} is not supported", currentVersion);
            }

            errorMessage = "ERROR: Your GVFS version is no longer supported.  Install the latest and try again.";
            errorIsFatal = true;
            return false;
        }

        public abstract class ForExistingEnlistment : GVFSVerb
        {
            public ForExistingEnlistment(bool validateOrigin = true) : base(validateOrigin)
            {
            }

            [Value(
                0,
                Required = false,
                Default = "",
                MetaName = "Enlistment Root Path",
                HelpText = "Full or relative path to the GVFS enlistment root")]
            public override string EnlistmentRootPathParameter { get; set; }

            public sealed override void Execute()
            {
                this.Execute(authentication: null);
            }

            public void Execute(GitAuthentication authentication)
            {
                this.ValidatePathParameter(this.EnlistmentRootPathParameter);

                this.PreCreateEnlistment();
                GVFSEnlistment enlistment = this.CreateEnlistment(this.EnlistmentRootPathParameter, authentication);

                this.Execute(enlistment);
            }

            protected virtual void PreCreateEnlistment()
            {
            }

            protected abstract void Execute(GVFSEnlistment enlistment);

            protected void InitializeLocalCacheAndObjectsPaths(
                ITracer tracer,
                GVFSEnlistment enlistment,
                RetryConfig retryConfig,
                ServerGVFSConfig serverGVFSConfig,
                CacheServerInfo cacheServer)
            {
                string error;
                if (!RepoMetadata.TryInitialize(tracer, Path.Combine(enlistment.EnlistmentRoot, GVFSConstants.DotGVFS.Root), out error))
                {
                    this.ReportErrorAndExit(tracer, "Failed to initialize repo metadata: " + error);
                }

                this.InitializeCachePathsFromRepoMetadata(tracer, enlistment);

                // Note: Repos cloned with a version of GVFS that predates the local cache will not have a local cache configured
                if (!string.IsNullOrWhiteSpace(enlistment.LocalCacheRoot))
                {
                    this.EnsureLocalCacheIsHealthy(tracer, enlistment, retryConfig, serverGVFSConfig, cacheServer);
                }

                RepoMetadata.Shutdown();
            }

            private void InitializeCachePathsFromRepoMetadata(
                ITracer tracer,
                GVFSEnlistment enlistment)
            {
                string error;
                string gitObjectsRoot;
                if (!RepoMetadata.Instance.TryGetGitObjectsRoot(out gitObjectsRoot, out error))
                {
                    this.ReportErrorAndExit(tracer, "Failed to determine git objects root from repo metadata: " + error);
                }

                if (string.IsNullOrWhiteSpace(gitObjectsRoot))
                {
                    this.ReportErrorAndExit(tracer, "Invalid git objects root (empty or whitespace)");
                }

                string localCacheRoot;
                if (!RepoMetadata.Instance.TryGetLocalCacheRoot(out localCacheRoot, out error))
                {
                    this.ReportErrorAndExit(tracer, "Failed to determine local cache path from repo metadata: " + error);
                }

                // Note: localCacheRoot is allowed to be empty, this can occur when upgrading from disk layout version 11 to 12

                string blobSizesRoot;
                if (!RepoMetadata.Instance.TryGetBlobSizesRoot(out blobSizesRoot, out error))
                {
                    this.ReportErrorAndExit(tracer, "Failed to determine blob sizes root from repo metadata: " + error);
                }

                if (string.IsNullOrWhiteSpace(blobSizesRoot))
                {
                    this.ReportErrorAndExit(tracer, "Invalid blob sizes root (empty or whitespace)");
                }

                enlistment.InitializeCachePaths(localCacheRoot, gitObjectsRoot, blobSizesRoot);
            }

            private void EnsureLocalCacheIsHealthy(
                ITracer tracer,
                GVFSEnlistment enlistment,
                RetryConfig retryConfig,
                ServerGVFSConfig serverGVFSConfig,
                CacheServerInfo cacheServer)
            {
                if (!Directory.Exists(enlistment.LocalCacheRoot))
                {
                    try
                    {
                        tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Local cache root: {enlistment.LocalCacheRoot} missing, recreating it");
                        Directory.CreateDirectory(enlistment.LocalCacheRoot);
                    }
                    catch (Exception e)
                    {
                        EventMetadata metadata = new EventMetadata();
                        metadata.Add("Exception", e.ToString());
                        metadata.Add("enlistment.LocalCacheRoot", enlistment.LocalCacheRoot);
                        tracer.RelatedError(metadata, $"{nameof(this.EnsureLocalCacheIsHealthy)}: Exception while trying to create local cache root");

                        this.ReportErrorAndExit(tracer, "Failed to create local cache: " + enlistment.LocalCacheRoot);
                    }
                }

                // Validate that the GitObjectsRoot directory is on disk, and that the GVFS repo is configured to use it.
                // If the directory is missing (and cannot be found in the mapping file) a new key for the repo will be added
                // to the mapping file and used for BOTH the GitObjectsRoot and BlobSizesRoot
                PhysicalFileSystem fileSystem = new PhysicalFileSystem();
                if (Directory.Exists(enlistment.GitObjectsRoot))
                {
                    bool gitObjectsRootInAlternates = false;

                    string alternatesFilePath = this.GetAlternatesPath(enlistment);
                    if (File.Exists(alternatesFilePath))
                    {
                        try
                        {
                            using (Stream stream = fileSystem.OpenFileStream(
                                alternatesFilePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite,
                                callFlushFileBuffers: false))
                            {
                                using (StreamReader reader = new StreamReader(stream))
                                {
                                    while (!reader.EndOfStream)
                                    {
                                        string alternatesLine = reader.ReadLine();
                                        if (string.Equals(alternatesLine, enlistment.GitObjectsRoot, StringComparison.OrdinalIgnoreCase))
                                        {
                                            gitObjectsRootInAlternates = true;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            EventMetadata exceptionMetadata = new EventMetadata();
                            exceptionMetadata.Add("Exception", e.ToString());
                            tracer.RelatedError(exceptionMetadata, $"{nameof(this.EnsureLocalCacheIsHealthy)}: Exception while trying to validate alternates file");

                            this.ReportErrorAndExit(tracer, $"Failed to validate that alternates file includes git objects root: {e.Message}");
                        }
                    }
                    else
                    {
                        tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Alternates file not found");
                    }

                    if (!gitObjectsRootInAlternates)
                    {
                        tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: GitObjectsRoot ({enlistment.GitObjectsRoot}) missing from alternates files, recreating alternates");
                        string error;
                        if (!this.TryCreateAlternatesFile(fileSystem, enlistment, out error))
                        {
                            this.ReportErrorAndExit(tracer, $"Failed to update alternates file to include git objects root: {error}");
                        }
                    }
                }
                else
                {
                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: GitObjectsRoot ({enlistment.GitObjectsRoot}) missing, determining new root");

                    if (cacheServer == null)
                    {
                        cacheServer = CacheServerResolver.GetCacheServerFromConfig(enlistment);
                    }

                    string error;
                    if (serverGVFSConfig == null)
                    {
                        if (retryConfig == null)
                        {
                            if (!RetryConfig.TryLoadFromGitConfig(tracer, enlistment, out retryConfig, out error))
                            {
                                this.ReportErrorAndExit(tracer, "Failed to determine GVFS timeout and max retries: " + error);
                            }
                        }

                        serverGVFSConfig = this.QueryGVFSConfig(tracer, enlistment, retryConfig);
                    }

                    string localCacheKey;
                    LocalCacheResolver localCacheResolver = new LocalCacheResolver(enlistment);
                    if (!localCacheResolver.TryGetLocalCacheKeyFromLocalConfigOrRemoteCacheServers(
                        tracer,
                        serverGVFSConfig,
                        cacheServer,
                        enlistment.LocalCacheRoot,
                        localCacheKey: out localCacheKey,
                        errorMessage: out error))
                    {
                        this.ReportErrorAndExit(tracer, $"Previous git objects root ({enlistment.GitObjectsRoot}) not found, and failed to determine new local cache key: {error}");
                    }

                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("localCacheRoot", enlistment.LocalCacheRoot);
                    metadata.Add("localCacheKey", localCacheKey);
                    metadata.Add(TracingConstants.MessageKey.InfoMessage, "Initializing and persisting updated paths");
                    tracer.RelatedEvent(EventLevel.Informational, "GVFSVerb_EnsureLocalCacheIsHealthy_InitializePathsFromKey", metadata);
                    enlistment.InitializeCachePathsFromKey(enlistment.LocalCacheRoot, localCacheKey);

                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Creating GitObjectsRoot ({enlistment.GitObjectsRoot}), GitPackRoot ({enlistment.GitPackRoot}), and BlobSizesRoot ({enlistment.BlobSizesRoot})");
                    try
                    {
                        Directory.CreateDirectory(enlistment.GitObjectsRoot);
                        Directory.CreateDirectory(enlistment.GitPackRoot);
                    }
                    catch (Exception e)
                    {
                        EventMetadata exceptionMetadata = new EventMetadata();
                        exceptionMetadata.Add("Exception", e.ToString());
                        exceptionMetadata.Add("enlistment.LocalCacheRoot", enlistment.LocalCacheRoot);
                        exceptionMetadata.Add("enlistment.GitObjectsRoot", enlistment.GitObjectsRoot);
                        exceptionMetadata.Add("enlistment.GitPackRoot", enlistment.GitPackRoot);
                        exceptionMetadata.Add("enlistment.BlobSizesRoot", enlistment.BlobSizesRoot);
                        tracer.RelatedError(exceptionMetadata, $"{nameof(this.InitializeLocalCacheAndObjectsPaths)}: Exception while trying to create objects, pack, and sizes folders");

                        this.ReportErrorAndExit(tracer, "Failed to create objects, pack, and sizes folders");
                    }

                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Creating new alternates file");
                    if (!this.TryCreateAlternatesFile(fileSystem, enlistment, out error))
                    {
                        this.ReportErrorAndExit(tracer, $"Failed to update alterates file with new objects path: {error}");
                    }

                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Saving git objects root ({enlistment.GitObjectsRoot}) in repo metadata");
                    RepoMetadata.Instance.SetGitObjectsRoot(enlistment.GitObjectsRoot);

                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: Saving blob sizes root ({enlistment.BlobSizesRoot}) in repo metadata");
                    RepoMetadata.Instance.SetBlobSizesRoot(enlistment.BlobSizesRoot);
                }

                // Validate that the BlobSizesRoot folder is on disk.
                // Note that if a user performed an action that resulted in the entire .gvfscache being deleted, the code above
                // for validating GitObjectsRoot will have already taken care of generating a new key and setting a new enlistment.BlobSizesRoot path
                if (!Directory.Exists(enlistment.BlobSizesRoot))
                {
                    tracer.RelatedInfo($"{nameof(this.EnsureLocalCacheIsHealthy)}: BlobSizesRoot ({enlistment.BlobSizesRoot}) not found, re-creating");
                    try
                    {
                        Directory.CreateDirectory(enlistment.BlobSizesRoot);
                    }
                    catch (Exception e)
                    {
                        EventMetadata exceptionMetadata = new EventMetadata();
                        exceptionMetadata.Add("Exception", e.ToString());
                        exceptionMetadata.Add("enlistment.BlobSizesRoot", enlistment.BlobSizesRoot);
                        tracer.RelatedError(exceptionMetadata, $"{nameof(this.InitializeLocalCacheAndObjectsPaths)}: Exception while trying to create blob sizes folder");

                        this.ReportErrorAndExit(tracer, "Failed to create blob sizes folder");
                    }
                }
            }

            private GVFSEnlistment CreateEnlistment(string enlistmentRootPath, GitAuthentication authentication)
            {
                string gitBinPath = GVFSPlatform.Instance.GitInstallation.GetInstalledGitBinPath();
                if (string.IsNullOrWhiteSpace(gitBinPath))
                {
                    this.ReportErrorAndExit("Error: " + GVFSConstants.GitIsNotInstalledError);
                }

                string hooksPath = null;
                if (GVFSPlatform.Instance.UnderConstruction.RequiresDeprecatedGitHooksLoader)
                {
                    // On Windows, the soon-to-be deprecated GitHooksLoader tries to call out to the hooks process without
                    // its full path, so we have to pass the path along to our background git processes via the PATH
                    // environment variable. On Mac this is not needed because we just copy our own hook directly into
                    // the .git/hooks folder, and once Windows does the same, this hooksPath can be removed (from here
                    // and all the classes that handle it on the way to GitProcess)

                    hooksPath = ProcessHelper.WhereDirectory(GVFSPlatform.Instance.Constants.GVFSHooksExecutableName);
                    if (hooksPath == null)
                    {
                        this.ReportErrorAndExit("Could not find " + GVFSPlatform.Instance.Constants.GVFSHooksExecutableName);
                    }
                }

                GVFSEnlistment enlistment = null;
                try
                {
                    if (this.validateOriginURL)
                    {
                        enlistment = GVFSEnlistment.CreateFromDirectory(enlistmentRootPath, gitBinPath, hooksPath, authentication);
                    }
                    else
                    {
                        enlistment = GVFSEnlistment.CreateWithoutRepoUrlFromDirectory(enlistmentRootPath, gitBinPath, hooksPath, authentication);
                    }

                    if (enlistment == null)
                    {
                        this.ReportErrorAndExit(
                            "Error: '{0}' is not a valid GVFS enlistment",
                            enlistmentRootPath);
                    }
                }
                catch (InvalidRepoException e)
                {
                    this.ReportErrorAndExit(
                        "Error: '{0}' is not a valid GVFS enlistment. {1}",
                        enlistmentRootPath,
                        e.Message);
                }

                return enlistment;
            }
        }

        public abstract class ForNoEnlistment : GVFSVerb
        {
            public ForNoEnlistment(bool validateOrigin = true) : base(validateOrigin)
            {
            }

            public override string EnlistmentRootPathParameter
            {
                get { throw new InvalidOperationException(); }
                set { throw new InvalidOperationException(); }
            }
        }

        public class VerbAbortedException : Exception
        {
            public VerbAbortedException(GVFSVerb verb)
            {
                this.Verb = verb;
            }

            public GVFSVerb Verb { get; }
        }
    }
}