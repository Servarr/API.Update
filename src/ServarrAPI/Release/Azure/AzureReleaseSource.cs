using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Octokit;
using ServarrAPI.Extensions;
using ServarrAPI.Model;
using ServarrAPI.Util;

namespace ServarrAPI.Release.Azure
{
    public class AzureReleaseSource : ReleaseSourceBase
    {
        private readonly string[] _packageArtifactNames = { "Packages", "WindowsInstaller" };

        private static readonly Regex ReleaseFeaturesGroup = new Regex(@"^New:\s*(?<text>.*?)\r*$", RegexOptions.Compiled);
        private static readonly Regex ReleaseFixesGroup = new Regex(@"^Fixed:\s*(?<text>.*?)\r*$", RegexOptions.Compiled);

        private static int? _lastBuildId;

        private readonly int[] _buildPipelines;

        private readonly Config _config;
        private readonly IUpdateService _updateService;
        private readonly IUpdateFileService _updateFileService;
        private readonly GitHubClient _githubClient;
        private readonly HttpClient _httpClient;
        private readonly VssConnection _connection;

        private readonly ILogger<AzureReleaseSource> _logger;

        public AzureReleaseSource(IUpdateService updateService,
                                  IUpdateFileService updateFileService,
                                  IOptions<Config> config,
                                  ILogger<AzureReleaseSource> logger)
        {
            _updateService = updateService;
            _updateFileService = updateFileService;
            _config = config.Value;

            var azureOrg = _config.AzureOrg ?? _config.Project;
            _buildPipelines = new int[] { _config.AzurePipelineId > 0 ? _config.AzurePipelineId : 1 };

            _connection = new VssConnection(new Uri($"https://dev.azure.com/{azureOrg}"), new VssBasicCredential());
            _githubClient = new GitHubClient(new ProductHeaderValue("ServarrAPI"));
            _httpClient = new HttpClient();

            _logger = logger;
        }

        protected override async Task<List<string>> DoFetchReleasesAsync()
        {
            var updated = new HashSet<string>();

            var buildClient = _connection.GetClient<BuildHttpClient>();
            var nightlyHistory = await buildClient.GetBuildsAsync(project: _config.Project,
                                                                  definitions: _buildPipelines,
                                                                  branchName: "refs/heads/develop",
                                                                  reasonFilter: BuildReason.IndividualCI | BuildReason.Manual,
                                                                  statusFilter: BuildStatus.Completed,
                                                                  resultFilter: BuildResult.Succeeded,
                                                                  queryOrder: BuildQueryOrder.StartTimeDescending,
                                                                  top: 5).ConfigureAwait(false);

            var branchHistory = await buildClient.GetBuildsAsync(project: _config.Project,
                                                                 definitions: _buildPipelines,
                                                                 reasonFilter: BuildReason.PullRequest | BuildReason.Manual | BuildReason.IndividualCI,
                                                                 statusFilter: BuildStatus.Completed,
                                                                 resultFilter: BuildResult.Succeeded,
                                                                 queryOrder: BuildQueryOrder.StartTimeDescending,
                                                                 top: 10).ConfigureAwait(false);

            var history = nightlyHistory.Concat(branchHistory).DistinctBy(x => x.Id).OrderByDescending(x => x.Id);

            // Store here temporarily so we don't break on not processed builds.
            var lastBuild = _lastBuildId;

            // URL query has filtered to most recent 5 successful, completed builds
            foreach (var build in history)
            {
                if (lastBuild.HasValue && lastBuild.Value >= build.Id)
                {
                    break;
                }

                // Extract the build version
                _logger.LogInformation($"Found version: {build.BuildNumber}");

                // Get the branch - either PR source branch or the actual brach
                string branch = null;
                if (build.SourceBranch.StartsWith("refs/heads/"))
                {
                    branch = build.SourceBranch.Replace("refs/heads/", string.Empty);
                }
                else if (build.SourceBranch.StartsWith("refs/pull/"))
                {
                    var success = int.TryParse(build.SourceBranch.Split("/")[2], out var prNum);
                    if (!success)
                    {
                        continue;
                    }

                    var githubOrg = _config.GithubOrg ?? _config.Project;
                    var pr = await _githubClient.PullRequest.Get(githubOrg, _config.Project, prNum).ConfigureAwait(false);

                    if (pr.Head.Repository.Fork)
                    {
                        continue;
                    }

                    branch = pr.Head.Ref;
                }
                else
                {
                    continue;
                }

                // If the branch is call develop (conflicts with daily develop builds)
                // or branch is called master (will get picked up when the github release goes up)
                // then skip
                if (branch == "nightly" || branch == "master")
                {
                    _logger.LogInformation($"Skipping azure build with branch {branch}");
                    continue;
                }

                // On azure, develop -> nightly
                if (branch == "develop")
                {
                    branch = "nightly";
                }

                _logger.LogInformation($"Found branch for version {build.BuildNumber}: {branch}");

                // Get build changes
                var changesTask = buildClient.GetBuildChangesAsync(_config.Project, build.Id);

                // Grab artifacts
                var artifacts = await buildClient.GetArtifactsAsync(_config.Project, build.Id).ConfigureAwait(false);

                // there should be a single artifact called 'Packages' we parse for packages
                var packageArtifacts = artifacts.Where(x => _packageArtifactNames.Contains(x.Name));
                if (packageArtifacts == null || !packageArtifacts.Any())
                {
                    continue;
                }

                var artifactClient = _connection.GetClient<ArtifactHttpClient>();
                var files = new List<AzureFile>();

                foreach (var artifact in packageArtifacts)
                {
                    files.AddRange(await artifactClient.GetArtifactFiles(_config.Project, build.Id, artifact).ConfigureAwait(false));
                }

                // Get an updateEntity
                var updateEntity = await _updateService.Find(build.BuildNumber, branch).ConfigureAwait(false);

                if (updateEntity != null)
                {
                    continue;
                }

                var version = Version.Parse(build.BuildNumber);

                // Create update object
                updateEntity = new UpdateEntity
                {
                    Version = build.BuildNumber,
                    IntVersion = version.ToIntVersion(),
                    ReleaseDate = build.StartTime.Value,
                    Branch = branch
                };

                updated.Add(branch);

                // Parse changes
                var changes = await changesTask.ConfigureAwait(false);
                var features = changes.Select(x => ReleaseFeaturesGroup.Match(x.Message));
                if (features.Any(x => x.Success))
                {
                    updateEntity.New.Clear();

                    foreach (var match in features.Where(x => x.Success))
                    {
                        updateEntity.New.Add(match.Groups["text"].Value);
                    }
                }

                var fixes = changes.Select(x => ReleaseFixesGroup.Match(x.Message));
                if (fixes.Any(x => x.Success))
                {
                    updateEntity.Fixed.Clear();

                    foreach (var match in fixes.Where(x => x.Success))
                    {
                        updateEntity.Fixed.Add(match.Groups["text"].Value);
                    }
                }

                await _updateService.Insert(updateEntity).ConfigureAwait(false);

                await Task.WhenAll(files.Select(x => ProcessFile(x, branch, updateEntity.Id))).ConfigureAwait(false);

                // Make sure we atleast skip this build next time.
                if (_lastBuildId == null ||
                    _lastBuildId.Value < build.Id)
                {
                    _lastBuildId = build.Id;
                }
            }

            return updated.ToList();
        }

        private async Task ProcessFile(AzureFile file, string branch, int updateId)
        {
            _logger.LogDebug("Processing {0}", file.Path);

            // Detect target operating system.
            var operatingSystem = Parser.ParseOS(file.Path);
            if (!operatingSystem.HasValue)
            {
                return;
            }

            _logger.LogDebug("Got os {0}", operatingSystem);

            // Detect runtime / arch
            var runtime = Parser.ParseRuntime(file.Path);
            _logger.LogDebug("Got runtime {0}", runtime);

            var arch = Parser.ParseArchitecture(file.Path);
            _logger.LogDebug("Got arch {0}", arch);

            var installer = Parser.ParseInstaller(file.Path);
            _logger.LogDebug("Got installer {0}", installer);

            // Calculate the hash of the zip file.
            var releaseFileName = Path.GetFileName(file.Path);
            var releaseZip = Path.Combine(_config.DataDirectory, branch, releaseFileName);
            string releaseHash;

            if (!File.Exists(releaseZip))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(releaseZip));

                using var fileStream = File.OpenWrite(releaseZip);
                using var artifactStream = await _httpClient.GetStreamAsync(file.Url).ConfigureAwait(false);
                await artifactStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            using (var stream = File.OpenRead(releaseZip))
            {
                using var sha = SHA256.Create();
                releaseHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLower();
            }

            File.Delete(releaseZip);

            // Add to database.
            var updateFile = new UpdateFileEntity
            {
                UpdateId = updateId,
                OperatingSystem = operatingSystem.Value,
                Architecture = arch,
                Runtime = runtime,
                Filename = releaseFileName,
                Url = file.Url,
                Hash = releaseHash,
                Installer = installer
            };

            await _updateFileService.Insert(updateFile).ConfigureAwait(false);
        }
    }
}
