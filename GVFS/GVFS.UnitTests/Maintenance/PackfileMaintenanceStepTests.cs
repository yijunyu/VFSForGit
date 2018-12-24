﻿using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Maintenance;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using GVFS.UnitTests.Mock.FileSystem;
using GVFS.UnitTests.Mock.Git;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace GVFS.UnitTests.Maintenance
{
    [TestFixture]
    public class PackfileMaintenanceStepTests
    {
        private const string StaleIdxName = "pack-stale.idx";
        private const string KeepName = "pack-3.keep";
        private MockTracer tracer;
        private MockGitProcess gitProcess;
        private GVFSContext context;

        private string ExpireCommand => $"multi-pack-index expire --object-dir=\"{this.context.Enlistment.GitObjectsRoot}\"";
        private string RepackCommand => $"-c pack.depth=0 -c pack.window=0 multi-pack-index repack --object-dir=\"{this.context.Enlistment.GitObjectsRoot}\" --batch-size=2g";

        [TestCase]
        public void PackfileMaintenanceIgnoreTimeRestriction()
        {
            this.TestSetup(DateTime.UtcNow);

            PackfileMaintenanceStep step = new PackfileMaintenanceStep(this.context, requireObjectCacheLock: false, forceRun: true);
            step.Execute();

            this.tracer.StartActivityTracer.RelatedErrorEvents.Count.ShouldEqual(0);
            this.tracer.StartActivityTracer.RelatedWarningEvents.Count.ShouldEqual(0);
            List<string> commands = this.gitProcess.CommandsRun;
            commands.Count.ShouldEqual(2);
            commands[0].ShouldEqual(this.ExpireCommand);
            commands[1].ShouldEqual(this.RepackCommand);
        }

        [TestCase]
        public void PackfileMaintenanceFailTimeRestriction()
        {
            this.TestSetup(DateTime.UtcNow);

            PackfileMaintenanceStep step = new PackfileMaintenanceStep(this.context, requireObjectCacheLock: false, forceRun: false);
            step.Execute();

            this.tracer.StartActivityTracer.RelatedErrorEvents.Count.ShouldEqual(0);
            this.tracer.StartActivityTracer.RelatedWarningEvents.Count.ShouldEqual(1);
            List<string> commands = this.gitProcess.CommandsRun;
            commands.Count.ShouldEqual(0);
        }

        [TestCase]
        public void PackfileMaintenancePassTimeRestriction()
        {
            this.TestSetup(DateTime.UtcNow.AddDays(-1));

            PackfileMaintenanceStep step = new PackfileMaintenanceStep(this.context, requireObjectCacheLock: false, forceRun: false);
            step.Execute();

            this.tracer.StartActivityTracer.RelatedErrorEvents.Count.ShouldEqual(0);
            this.tracer.StartActivityTracer.RelatedWarningEvents.Count.ShouldEqual(0);
            List<string> commands = this.gitProcess.CommandsRun;
            commands.Count.ShouldEqual(2);
            commands[0].ShouldEqual(this.ExpireCommand);
            commands[1].ShouldEqual(this.RepackCommand);
        }

        [TestCase]
        public void CountPackFiles()
        {
            this.TestSetup(DateTime.UtcNow);

            PackfileMaintenanceStep step = new PackfileMaintenanceStep(this.context, requireObjectCacheLock: false, forceRun: false);

            step.GetPackFilesInfo(out int count, out long size, out bool hasKeep);
            count.ShouldEqual(3);
            size.ShouldEqual(11);
            hasKeep.ShouldEqual(true);

            this.context.FileSystem.DeleteFile(Path.Combine(this.context.Enlistment.GitPackRoot, KeepName));

            step.GetPackFilesInfo(out count, out size, out hasKeep);
            count.ShouldEqual(3);
            size.ShouldEqual(11);
            hasKeep.ShouldEqual(false);
        }

        [TestCase]
        public void CleanStaleIdxFiles()
        {
            this.TestSetup(DateTime.UtcNow);

            PackfileMaintenanceStep step = new PackfileMaintenanceStep(this.context, requireObjectCacheLock: false, forceRun: false);

            List<string> staleIdx = step.CleanStaleIdxFiles(out int numDeletionBlocked);

            staleIdx.Count.ShouldEqual(1);
            staleIdx[0].ShouldEqual(StaleIdxName);

            this.context
                .FileSystem
                .FileExists(Path.Combine(this.context.Enlistment.GitPackRoot, StaleIdxName))
                .ShouldBeFalse();
        }

        private void TestSetup(DateTime lastRun)
        {
            string lastRunTime = EpochConverter.ToUnixEpochSeconds(lastRun).ToString();

            this.gitProcess = new MockGitProcess();

            // Create enlistment using git process
            GVFSEnlistment enlistment = new MockGVFSEnlistment(this.gitProcess);

            // Create a last run time file
            MockFile timeFile = new MockFile(Path.Combine(enlistment.GitObjectsRoot, "info", PackfileMaintenanceStep.PackfileLastRunFileName), lastRunTime);

            // Create info directory to hold last run time file
            MockDirectory info = new MockDirectory(
                Path.Combine(enlistment.GitObjectsRoot, "info"),
                null,
                new List<MockFile>() { timeFile });

            // Create pack info
            MockDirectory pack = new MockDirectory(
                enlistment.GitPackRoot,
                null,
                new List<MockFile>()
                {
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-1.pack"), "one"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-1.idx"), "1"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-2.pack"), "two"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-2.idx"), "2"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-3.pack"), "three"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, "pack-3.idx"), "3"),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, KeepName), string.Empty),
                    new MockFile(Path.Combine(enlistment.GitPackRoot, StaleIdxName), "4"),
                });

            // Create git objects directory
            MockDirectory gitObjectsRoot = new MockDirectory(enlistment.GitObjectsRoot, new List<MockDirectory>() { info, pack }, null);

            // Add object directory to file System
            List<MockDirectory> directories = new List<MockDirectory>() { gitObjectsRoot };
            PhysicalFileSystem fileSystem = new MockFileSystem(new MockDirectory(enlistment.EnlistmentRoot, directories, null));

            // Create and return Context
            this.tracer = new MockTracer();
            this.context = new GVFSContext(this.tracer, fileSystem, repository: null, enlistment: enlistment);

            this.gitProcess.SetExpectedCommandResult(
                this.ExpireCommand,
                () => new GitProcess.Result(string.Empty, string.Empty, GitProcess.Result.SuccessCode));
            this.gitProcess.SetExpectedCommandResult(
                this.RepackCommand,
                () => new GitProcess.Result(string.Empty, string.Empty, GitProcess.Result.SuccessCode));
        }
    }
}
