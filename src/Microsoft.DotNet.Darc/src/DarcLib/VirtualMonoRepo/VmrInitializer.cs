// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Darc.Models.VirtualMonoRepo;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.Extensions.Logging;

#nullable enable
namespace Microsoft.DotNet.DarcLib.VirtualMonoRepo;

/// <summary>
/// This class is able to initialize an individual repository within the VMR for the first time.
/// It pulls in the new sources adhering to cloaking rules, accommodating for patched files, resolving submodules.
/// It can also initialize all other repositories recursively based on the dependencies stored in Version.Details.xml.
/// </summary>
public class VmrInitializer : VmrManagerBase, IVmrInitializer
{
    // Message shown when initializing an individual repo for the first time
    private const string InitializationCommitMessage =
        $$"""
        [{name}] Initial pull of the individual repository ({newShaShort})

        Original commit: {remote}/commit/{newSha}

        {{AUTOMATION_COMMIT_TAG}}
        """;

    // Message used when finalizing the initialization with a merge commit
    private const string MergeCommitMessage =
        $$"""
        Recursive initialization for {name} / {newShaShort}

        {{AUTOMATION_COMMIT_TAG}}
        """;

    private readonly IVmrInfo _vmrInfo;
    private readonly IVmrDependencyTracker _dependencyTracker;
    private readonly IVmrPatchHandler _patchHandler;
    private readonly IRepositoryCloneManager _cloneManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VmrUpdater> _logger;

    public VmrInitializer(
        IVmrDependencyTracker dependencyTracker,
        IVmrPatchHandler patchHandler,
        IVersionDetailsParser versionDetailsParser,
        IRepositoryCloneManager cloneManager,
        IThirdPartyNoticesGenerator thirdPartyNoticesGenerator,
        IReadmeComponentListGenerator readmeComponentListGenerator,
        ILocalGitRepo localGitClient,
        IGitFileManagerFactory gitFileManagerFactory,
        IFileSystem fileSystem,
        ILogger<VmrUpdater> logger,
        ISourceManifest sourceManifest,
        IVmrInfo vmrInfo)
        : base(vmrInfo, sourceManifest, dependencyTracker, patchHandler, versionDetailsParser, thirdPartyNoticesGenerator, readmeComponentListGenerator, localGitClient, gitFileManagerFactory, fileSystem, logger)
    {
        _vmrInfo = vmrInfo;
        _dependencyTracker = dependencyTracker;
        _patchHandler = patchHandler;
        _cloneManager = cloneManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task InitializeRepository(
        string mappingName,
        string? targetRevision,
        string? targetVersion,
        bool initializeDependencies,
        LocalPath sourceMappingsPath,
        IReadOnlyCollection<AdditionalRemote> additionalRemotes,
        CancellationToken cancellationToken)
    {
        await _dependencyTracker.InitializeSourceMappings(sourceMappingsPath);

        var mapping = _dependencyTracker.Mappings.FirstOrDefault(m => m.Name == mappingName)
            ?? throw new Exception($"No repository mapping named `{mappingName}` found!");

        if (_dependencyTracker.GetDependencyVersion(mapping) is not null)
        {
            throw new EmptySyncException($"Repository {mapping.Name} already exists");
        }

        var workBranch = CreateWorkBranch($"init/{mapping.Name}{(targetRevision != null ? $"/{targetRevision}" : string.Empty)}");

        var rootUpdate = new VmrDependencyUpdate(
            mapping,
            mapping.DefaultRemote,
            targetRevision ?? mapping.DefaultRef,
            targetVersion,
            null);

        try
        {
            IEnumerable<VmrDependencyUpdate> updates = initializeDependencies
            ? await GetAllDependencies(rootUpdate, additionalRemotes, cancellationToken)
            : new[] { rootUpdate };

            foreach (var update in updates)
            {
                if (_fileSystem.DirectoryExists(_vmrInfo.GetRepoSourcesPath(update.Mapping)))
                {
                    // Repository has already been initialized
                    continue;
                }

                await InitializeRepository(update, additionalRemotes, cancellationToken);
            }
        }
        catch (Exception)
        {
            _logger.LogWarning(
                InterruptedSyncExceptionMessage,
                workBranch.OriginalBranch.StartsWith("sync") || workBranch.OriginalBranch.StartsWith("init") ?
                "the original" : workBranch.OriginalBranch);
            throw;
        }

        string newSha = _dependencyTracker.GetDependencyVersion(mapping)!.Sha;

        var commitMessage = PrepareCommitMessage(MergeCommitMessage, mapping.Name, mapping.DefaultRemote, oldSha: null, newSha);
        workBranch.MergeBack(commitMessage);

        _logger.LogInformation("Recursive initialization for {repo} / {sha} finished", mapping.Name, newSha);
    }

    private async Task InitializeRepository(
        VmrDependencyUpdate update,
        IReadOnlyCollection<AdditionalRemote> additionalRemotes,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing {name} at {revision}..", update.Mapping.Name, update.TargetRevision);

        var remotes = additionalRemotes
            .Where(r => r.Mapping == update.Mapping.Name)
            .Select(r => r.RemoteUri)
            .Prepend(update.RemoteUri)
            .ToArray();

        var clonePath = _cloneManager.PrepareClone(
            update.Mapping,
            remotes,
            update.TargetRevision,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        update = update with
        {
            TargetRevision = GetShaForRef(clonePath, update.TargetRevision == HEAD ? null : update.TargetRevision)
        };

        string commitMessage = PrepareCommitMessage(InitializationCommitMessage, update.Mapping.Name, update.RemoteUri, newSha: update.TargetRevision);

        await UpdateRepoToRevision(
            update,
            clonePath,
            Constants.EmptyGitObject,
            DotnetBotCommitSignature,
            commitMessage,
            reapplyVmrPatches: true,
            cancellationToken);

        _logger.LogInformation("Initialization of {name} finished", update.Mapping.Name);
    }

    protected override Task<IReadOnlyCollection<VmrIngestionPatch>> RestoreVmrPatchedFiles(
        SourceMapping mapping,
        IReadOnlyCollection<VmrIngestionPatch> patches,
        CancellationToken cancellationToken)
    {
        // We only need to apply VMR patches that belong to the mapping, nothing to restore from before
        IReadOnlyCollection<VmrIngestionPatch> vmrPatchesForMapping = _patchHandler.GetVmrPatches(mapping)
            .Select(patch => new VmrIngestionPatch(patch, VmrInfo.GetRelativeRepoSourcesPath(mapping)))
            .ToImmutableArray();

        return Task.FromResult(vmrPatchesForMapping);
    }
}
