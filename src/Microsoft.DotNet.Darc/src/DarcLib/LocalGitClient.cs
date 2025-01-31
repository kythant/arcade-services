// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.DotNet.DarcLib.VirtualMonoRepo;
using Microsoft.Extensions.Logging;

#nullable enable
namespace Microsoft.DotNet.DarcLib;

public class LocalGitClient : ILocalGitRepo
{
    private readonly ILogger _logger;
    private readonly string _gitExecutable;

    /// <summary>
    ///     Construct a new local git client
    /// </summary>
    /// <param name="path">Current path</param>
    public LocalGitClient(string gitExecutable, ILogger logger)
    {
        _gitExecutable = gitExecutable;
        _logger = logger;
    }

    public async Task<string> GetFileContentsAsync(string relativeFilePath, string repoDir, string branch)
    {
        string fullPath = Path.Combine(repoDir, relativeFilePath);
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
        {
            string? parentTwoDirectoriesUp = Path.GetDirectoryName(Path.GetDirectoryName(fullPath));
            if (parentTwoDirectoriesUp != null && Directory.Exists(parentTwoDirectoriesUp))
            {
                throw new DependencyFileNotFoundException($"Found parent-directory path ('{parentTwoDirectoriesUp}') but unable to find specified file ('{relativeFilePath}')");
            }
            else
            {
                throw new DependencyFileNotFoundException($"Neither parent-directory path ('{parentTwoDirectoriesUp}') nor specified file ('{relativeFilePath}') found.");
            }
        }

        if (!File.Exists(fullPath))
        {
            throw new DependencyFileNotFoundException($"Could not find {fullPath}");
        }

        using (var streamReader = new StreamReader(fullPath))
        {
            return await streamReader.ReadToEndAsync();
        }
    }

    /// <summary>
    ///     Updates local copies of the files.
    /// </summary>
    /// <param name="filesToCommit">Files to update locally</param>
    /// <param name="repoUri">Base path of the repo</param>
    /// <param name="branch">Unused</param>
    /// <param name="commitMessage">Unused</param>
    public async Task CommitFilesAsync(List<GitFile> filesToCommit, string repoUri, string branch, string commitMessage)
    {
        string repoDir = LocalHelpers.GetRootDir(_gitExecutable, _logger);
        try
        {
            using (Repository localRepo = new Repository(repoDir))
            {
                foreach (GitFile file in filesToCommit)
                {
                    Debug.Assert(file != null, "Passed in a null GitFile in filesToCommit");
                    switch (file.Operation)
                    {
                        case GitFileOperation.Add:
                            var parentDirectoryInfo = Directory.GetParent(file.FilePath) 
                                ?? throw new Exception($"Cannot find parent directory of {file.FilePath}.");
                            
                            string parentDirectory = parentDirectoryInfo.FullName;

                            if (!Directory.Exists(parentDirectory))
                            {
                                Directory.CreateDirectory(parentDirectory);
                            }

                            string fullPath = Path.Combine(repoUri, file.FilePath);
                            using (var streamWriter = new StreamWriter(fullPath))
                            {
                                string finalContent;
                                switch (file.ContentEncoding)
                                {
                                    case ContentEncoding.Utf8:
                                        finalContent = file.Content;
                                        break;
                                    case ContentEncoding.Base64:
                                        byte[] bytes = Convert.FromBase64String(file.Content);
                                        finalContent = Encoding.UTF8.GetString(bytes);
                                        break;
                                    default:
                                        throw new DarcException($"Unknown file content encoding {file.ContentEncoding}");
                                }
                                finalContent = NormalizeLineEndings(fullPath, finalContent);
                                await streamWriter.WriteAsync(finalContent);

                                LibGit2SharpHelpers.AddFileToIndex(localRepo, file, fullPath, _logger);
                            }
                            break;
                        case GitFileOperation.Delete:
                            if (File.Exists(file.FilePath))
                            {
                                File.Delete(file.FilePath);
                            }
                            break;
                    }
                }
            }
        }
        catch (Exception exc)
        {
            throw new DarcException($"Something went wrong when checking out {repoUri} in {repoDir}", exc);
        }
    }

    /// <summary>
    /// Normalize line endings of content.
    /// </summary>
    /// <param name="filePath">Path of file</param>
    /// <param name="content">Content to normalize</param>
    /// <returns>Normalized content</returns>
    /// <remarks>
    ///     Normalize based on the following rules:
    ///     - Auto CRLF is assumed.
    ///     - Check the git attributes the file to determine whether it has a specific setting for the file.  If so, use that.
    ///     - If no setting, or if auto, then determine whether incoming content differs in line ends vs. the
    ///       OS setting, and replace if needed.
    /// </remarks>
    private string NormalizeLineEndings(string filePath, string content)
    {
        const string crlf = "\r\n";
        const string lf = "\n";
        // Check gitAttributes to determine whether the file has eof handling set.
        string eofAttr = LocalHelpers.ExecuteCommand(_gitExecutable, $"check-attr eol -- {filePath}", _logger);
        if (string.IsNullOrEmpty(eofAttr) ||
            eofAttr.Contains("eol: unspecified") ||
            eofAttr.Contains("eol: auto"))
        {
            if (Environment.NewLine != crlf)
            {
                return content.Replace(crlf, Environment.NewLine);
            }
            else if (Environment.NewLine == crlf && !content.Contains(crlf))
            {
                return content.Replace(lf, Environment.NewLine);
            }
        }
        else if (eofAttr.Contains("eol: crlf"))
        {
            // Test to avoid adding extra \r.
            if (!content.Contains(crlf))
            {
                return content.Replace(lf, crlf);
            }
        }
        else if (eofAttr.Contains("eol: lf"))
        {
            return content.Replace(crlf, lf);
        }
        else
        {
            throw new DarcException($"Unknown eof setting '{eofAttr}' for file '{filePath};");
        }
        return content;
    }

    /// <summary>
    ///     Checkout the repo to the specified state.
    /// </summary>
    /// <param name="commit">Tag, branch, or commit to checkout.</param>
    public void Checkout(string repoDir, string commit, bool force = false)
    {
        _logger.LogDebug($"Checking out {commit}", commit ?? "default commit");
        CheckoutOptions checkoutOptions = new CheckoutOptions
        {
            CheckoutModifiers = force ? CheckoutModifiers.Force : CheckoutModifiers.None,
        };
        try
        {
            _logger.LogDebug($"Reading local repo from {repoDir}");
            using (Repository localRepo = new Repository(repoDir))
            {
                if (commit == null)
                {
                    commit = localRepo.Head.Reference.TargetIdentifier;
                    _logger.LogInformation($"Repo {localRepo.Info.WorkingDirectory} default commit to checkout is {commit}");
                }
                try
                {
                    _logger.LogDebug($"Attempting to check out {commit} in {repoDir}");
                    LibGit2SharpHelpers.SafeCheckout(localRepo, commit, checkoutOptions, _logger);
                    if (force)
                    {
                        CleanRepoAndSubmodules(localRepo, _logger);
                    }
                }
                catch (NotFoundException)
                {
                    _logger.LogWarning($"Couldn't find commit {commit} in {repoDir} locally.  Attempting fetch.");
                    try
                    {
                        foreach (LibGit2Sharp.Remote r in localRepo.Network.Remotes)
                        {
                            IEnumerable<string> refSpecs = r.FetchRefSpecs.Select(x => x.Specification);
                            _logger.LogDebug($"Fetching {string.Join(";", refSpecs)} from {r.Url} in {repoDir}");
                            try
                            {
                                Commands.Fetch(localRepo, r.Name, refSpecs, new FetchOptions(), $"Fetching from {r.Url}");
                            }
                            catch
                            {
                                _logger.LogWarning($"Fetching failed, are you offline or missing a remote?");
                            }
                        }
                        _logger.LogDebug($"After fetch, attempting to checkout {commit} in {repoDir}");
                        LibGit2SharpHelpers.SafeCheckout(localRepo, commit, checkoutOptions, _logger);

                        if (force)
                        {
                            CleanRepoAndSubmodules(localRepo, _logger);
                        }
                    }
                    catch   // Most likely network exception, could also be no remotes.  We can't do anything about any error here.
                    {
                        _logger.LogError($"After fetch, still couldn't find commit or treeish {commit} in {repoDir}.  Are you offline or missing a remote?");
                        throw;
                    }
                }
            }
        }
        catch (Exception exc)
        {
            throw new Exception($"Something went wrong when checking out {commit} in {repoDir}", exc);
        }
    }
        
    public void Stage(string repoDir, string pathToStage)
    {
        using var repository = new Repository(repoDir);
        Commands.Stage(repository, pathToStage);
    }

    private static void CleanRepoAndSubmodules(Repository repo, ILogger log)
    {
        using (log.BeginScope($"Beginning clean of {repo.Info.WorkingDirectory} and {repo.Submodules.Count()} submodules"))
        {
            log.LogDebug($"Beginning clean of {repo.Info.WorkingDirectory} and {repo.Submodules.Count()} submodules");
            StatusOptions options = new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            };
            int count = 0;
            foreach (StatusEntry item in repo.RetrieveStatus(options))
            {
                if (item.State == FileStatus.NewInWorkdir)
                {
                    File.Delete(Path.Combine(repo.Info.WorkingDirectory, item.FilePath));
                    ++count;
                }
            }
            log.LogDebug($"Deleted {count} untracked files");

            foreach (Submodule sub in repo.Submodules)
            {
                string normalizedSubPath = sub.Path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                string subRepoPath = Path.Combine(repo.Info.WorkingDirectory, normalizedSubPath);
                string subRepoGitFilePath = Path.Combine(subRepoPath, ".git");
                if (!File.Exists(subRepoGitFilePath))
                {
                    log.LogDebug($"Submodule {sub.Name} in {subRepoPath} does not appear to be initialized (no file at {subRepoGitFilePath}), attempting to initialize now.");
                    // hasn't been initialized yet, can happen when different hashes have new or moved submodules
                    try
                    {
                        repo.Submodules.Update(sub.Name, new SubmoduleUpdateOptions { Init = true });
                    }
                    catch
                    {
                        log.LogDebug($"Submodule {sub.Name} in {subRepoPath} is already initialized, trying to adopt from super-repo {repo.Info.Path}");

                        // superrepo thinks it is initialized, but it's orphaned.  Go back to the master repo to find out where this is supposed to point.
                        using (Repository masterRepo = new Repository(repo.Info.WorkingDirectory))
                        {
                            Submodule masterSubModule = masterRepo.Submodules.Single(s => s.Name == sub.Name);
                            string masterSubPath = Path.Combine(repo.Info.Path, "modules", masterSubModule.Path);
                            log.LogDebug($"Writing .gitdir redirect {masterSubPath} to {subRepoGitFilePath}");
                            Directory.CreateDirectory(Path.GetDirectoryName(subRepoGitFilePath) ?? throw new Exception($"Cannot get directory name of {subRepoGitFilePath}"));
                            File.WriteAllText(subRepoGitFilePath, $"gitdir: {masterSubPath}");
                        }
                    }
                }

                using (log.BeginScope($"Beginning clean of submodule {sub.Name}"))
                {
                    log.LogDebug($"Beginning clean of submodule {sub.Name} in {subRepoPath}");

                    // The worktree is stored in the .gitdir/config file, so we have to change it
                    // to get it to check out to the correct place.
                    ConfigurationEntry<string>? oldWorkTree = null;
                    using (Repository subRepo = new Repository(subRepoPath))
                    {
                        oldWorkTree = subRepo.Config.Get<string>("core.worktree");
                        if (oldWorkTree != null)
                        {
                            log.LogDebug($"{subRepoPath} old worktree is {oldWorkTree.Value}, setting to {subRepoPath}");
                            subRepo.Config.Set("core.worktree", subRepoPath);
                        }
                        // This branch really shouldn't happen but just in case.
                        else
                        {
                            log.LogDebug($"{subRepoPath} has default worktree, leaving unchanged");
                        }
                    }

                    using (Repository subRepo = new Repository(subRepoPath))
                    {
                        log.LogDebug($"Resetting {sub.Name} to {sub.HeadCommitId.Sha}");
                        subRepo.Reset(ResetMode.Hard, subRepo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = subRepo.Refs }).Single(c => c.Sha == sub.HeadCommitId.Sha));
                        // Now we reset the worktree back so that when we can initialize a Repository
                        // from it, instead of having to figure out which hash of the repo was most recently checked out.
                        if (oldWorkTree != null)
                        {
                            log.LogDebug($"resetting {subRepoPath} worktree to {oldWorkTree.Value}");
                            subRepo.Config.Set("core.worktree", oldWorkTree.Value);
                        }
                        else
                        {
                            log.LogDebug($"leaving {subRepoPath} worktree as default");
                        }
                        log.LogDebug($"Done resetting {subRepoPath}, checking submodules");
                        CleanRepoAndSubmodules(subRepo, log);
                    }
                }

                if (File.Exists(subRepoGitFilePath))
                {
                    log.LogDebug($"Deleting {subRepoGitFilePath} to orphan submodule {sub.Name}");
                    File.Delete(subRepoGitFilePath);
                }
                else
                {
                    log.LogDebug($"{sub.Name} doesn't have a .gitdir redirect at {subRepoGitFilePath}, skipping delete");
                }
            }
        }
    }

    /// <summary>
    ///     Add a remote to a local repo if does not already exist, and attempt to fetch commits.
    /// </summary>
    /// <param name="repoDir">Path to a git repository</param>
    /// <param name="repoUrl">URL of the remote to add</param>
    /// <param name="skipFetch">Skip fetching remote changes</param>
    /// <returns>Name of the remote</returns>
    public string AddRemoteIfMissing(string repoDir, string repoUrl, bool skipFetch = false)
    {
        using var repo = new Repository(repoDir);
        return AddRemoteIfMissing(repo, repoUrl, skipFetch);
    }

    private string AddRemoteIfMissing(Repository repo, string repoUrl, bool skipFetch = false)
    {
        var remote = repo.Network.Remotes.FirstOrDefault(r => r.Url.Equals(repoUrl, StringComparison.InvariantCultureIgnoreCase));
        string remoteName;

        if (remote is not null)
        {
            remoteName = remote.Name;
        }
        else
        {
            _logger.LogDebug($"Adding {repoUrl} remote to {repo.Info.Path}");

            // Remote names don't matter much but should be stable
            remoteName = StringUtils.GetXxHash64(repoUrl);
            repo.Network.Remotes.Add(remoteName, repoUrl);
        }

        if (!skipFetch)
        {
            _logger.LogDebug($"Fetching new commits from {repoUrl} into {repo.Info.Path}");
            Commands.Fetch(
                repo,
                remoteName,
                new[] { $"+refs/heads/*:refs/remotes/{remoteName}/*" },
                new FetchOptions(),
                $"Fetching {repoUrl} into {repo.Info.Path}");
        }

        return remoteName;
    }

    public List<GitSubmoduleInfo> GetGitSubmodules(string repoDir, string commit)
    {
        if (commit == Constants.EmptyGitObject)
        {
            return new();
        }

        var repository = new Repository(repoDir);

        // I haven't found a way to do this with LibGit2Sharp without checking out the commit
        Commands.Checkout(repository, commit);

        return repository.Submodules
            .Select(s => new GitSubmoduleInfo(s.Name, s.Path, s.Url, s.IndexCommitId.Sha))
            .ToList();
    }

    public IEnumerable<string> GetStagedFiles(string repoDir)
    {
        using var repository = new Repository(repoDir);
        var repositoryStatus = repository.RetrieveStatus();
        return repositoryStatus.Added
            .Concat(repositoryStatus.Removed)
            .Concat(repositoryStatus.Staged)
            .Select(file => file.FilePath);
    }

    public void Push(
        string repoPath,
        string branchName,
        string remoteUrl, 
        string token,
        LibGit2Sharp.Identity? identity = null)
    {
        identity ??= new LibGit2Sharp.Identity(Constants.DarcBotName, Constants.DarcBotEmail);

        using var repo = new Repository(
            repoPath, 
            new RepositoryOptions { Identity = identity });

        var remoteName = AddRemoteIfMissing(repo, remoteUrl, true);
        var remote = repo.Network.Remotes[remoteName];

        var branch = repo.Branches[branchName];
        if (branch == null)
        {
            throw new Exception($"No branch {branchName} found in repo. {repo.Info.Path}");
        }
        
        var pushOptions = new PushOptions
        {
            CredentialsProvider = (url, user, cred) =>
                new UsernamePasswordCredentials
                {
                    Username = token,
                    Password = string.Empty
                }
        };

        repo.Network.Push(remote, branch.CanonicalName, pushOptions);

        _logger.LogInformation($"Pushed branch {branch} to remote {remote.Name}");
    }
}
