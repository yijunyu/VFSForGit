﻿using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GVFS.Common.Maintenance
{
    /// <summary>
    /// This step maintains the packfiles in the object cache.
    ///
    /// This is done in two steps:
    ///
    /// git multi-pack-index expire: This deletes the pack-files whose objects
    /// appear in newer pack-files. The multi-pack-index prevents git from
    /// looking at these packs. Rewrites the multi-pack-index to no longer
    /// refer to these (deleted) packs.
    ///
    /// git multi-pack-index repack --batch-size= inspects packs covered by the
    /// multi-pack-index in modified-time order(ascending). Greedily selects a
    /// batch of packs whose file sizes are all less than "size", but that sum
    /// up to at least "size". Then generate a new pack-file containing the
    /// objects that are uniquely referenced by the multi-pack-index.
    /// </summary>
    public class PackfileMaintenanceStep : GitMaintenanceStep
    {
        public const string PackfileLastRunFileName = "pack-maintenance.time";
        public const string DefaultBatchSize = "2g";
        private readonly bool forceRun;
        private readonly string batchSize;

        public PackfileMaintenanceStep(
            GVFSContext context,
            bool requireObjectCacheLock = true,
            bool forceRun = false,
            string batchSize = DefaultBatchSize)
            : base(context, requireObjectCacheLock)
        {
            this.forceRun = forceRun;
            this.batchSize = batchSize;
        }

        public override string Area => nameof(PackfileMaintenanceStep);
        protected override string LastRunTimeFilePath => Path.Combine(this.Context.Enlistment.GitObjectsRoot, "info", PackfileLastRunFileName);
        protected override TimeSpan TimeBetweenRuns => TimeSpan.FromDays(1);

        // public only for unit tests
        public void GetPackFilesInfo(out int count, out long size, out bool hasKeep)
        {
            count = 0;
            size = 0;
            hasKeep = false;

            foreach (DirectoryItemInfo info in this.Context.FileSystem.ItemsInDirectory(this.Context.Enlistment.GitPackRoot))
            {
                string extension = Path.GetExtension(info.Name);

                if (string.Equals(extension, ".pack", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    size += info.Length;
                }
                else if (string.Equals(extension, ".keep", StringComparison.OrdinalIgnoreCase))
                {
                    hasKeep = true;
                }
            }
        }

        // public only for unit tests
        public List<string> CleanStaleIdxFiles(out int numDeletionBlocked)
        {
            List<DirectoryItemInfo> packDirContents = this.Context
                                                          .FileSystem
                                                          .ItemsInDirectory(this.Context.Enlistment.GitPackRoot)
                                                          .ToList();

            numDeletionBlocked = 0;
            List<string> deletedIdxFiles = new List<string>();

            // If something (probably VFS for Git) has a handle open to a ".idx" file, then
            // the 'git multi-pack-index expire' command cannot delete it. We should come in
            // later and try to clean these up. Count those that we are able to delete and
            // those we still can't.

            foreach (DirectoryItemInfo info in packDirContents)
            {
                if (string.Equals(Path.GetExtension(info.Name), ".idx", StringComparison.OrdinalIgnoreCase))
                {
                    string pairedPack = Path.ChangeExtension(info.FullName, ".pack");

                    if (!this.Context.FileSystem.FileExists(pairedPack))
                    {
                        if (this.Context.FileSystem.TryDeleteFile(info.FullName))
                        {
                            deletedIdxFiles.Add(info.Name);
                        }
                        else
                        {
                            numDeletionBlocked++;
                        }
                    }
                }
            }

            return deletedIdxFiles;
        }

        protected override void PerformMaintenance()
        {
            using (ITracer activity = this.Context.Tracer.StartActivity(this.Area, EventLevel.Informational, Keywords.Telemetry, metadata: null))
            {
                // forceRun is only currently true for functional tests
                if (!this.forceRun)
                {
                    if (!this.EnoughTimeBetweenRuns())
                    {
                        activity.RelatedWarning($"Skipping {nameof(PackfileMaintenanceStep)} due to not enough time between runs");
                        return;
                    }

                    IEnumerable<int> processIds = this.RunningGitProcessIds();
                    if (processIds.Any())
                    {
                        activity.RelatedWarning($"Skipping {nameof(PackfileMaintenanceStep)} due to git pids {string.Join(",", processIds)}", Keywords.Telemetry);
                        return;
                    }
                }

                this.GetPackFilesInfo(out int beforeCount, out long beforeSize, out bool hasKeep);

                if (!hasKeep)
                {
                    activity.RelatedWarning(this.CreateEventMetadata(), "Skipping pack maintenance due to no .keep file.");
                    return;
                }

                GitProcess.Result expireResult = this.RunGitCommand((process) => process.MultiPackIndexExpire(this.Context.Enlistment.GitObjectsRoot));
                List<string> staleIdxFiles = this.CleanStaleIdxFiles(out int numDeletionBlocked);
                this.GetPackFilesInfo(out int expireCount, out long expireSize, out hasKeep);
                GitProcess.Result repackResult = this.RunGitCommand((process) => process.MultiPackIndexRepack(this.Context.Enlistment.GitObjectsRoot, this.batchSize));
                this.GetPackFilesInfo(out int afterCount, out long afterSize, out hasKeep);

                EventMetadata metadata = new EventMetadata();
                metadata.Add("GitObjectsRoot", this.Context.Enlistment.GitObjectsRoot);
                metadata.Add("BatchSize", this.batchSize);
                metadata.Add(nameof(beforeCount), beforeCount);
                metadata.Add(nameof(beforeSize), beforeSize);
                metadata.Add(nameof(expireCount), expireCount);
                metadata.Add(nameof(expireSize), expireSize);
                metadata.Add(nameof(afterCount), afterCount);
                metadata.Add(nameof(afterSize), afterSize);
                metadata.Add("ExpireOutput", expireResult.Output);
                metadata.Add("ExpireErrors", expireResult.Errors);
                metadata.Add("RepackOutput", repackResult.Output);
                metadata.Add("RepackErrors", repackResult.Errors);
                metadata.Add("NumStaleIdxFiles", staleIdxFiles.Count);
                metadata.Add("NumIdxDeletionsBlocked", numDeletionBlocked);
                activity.RelatedEvent(EventLevel.Informational, $"{this.Area}_{nameof(this.PerformMaintenance)}", metadata, Keywords.Telemetry);

                this.SaveLastRunTimeToFile();
            }
        }
    }
}
