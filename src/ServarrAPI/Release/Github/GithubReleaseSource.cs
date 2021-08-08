using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Octokit;
using ServarrAPI.Extensions;
using ServarrAPI.Model;
using ServarrAPI.Util;

namespace ServarrAPI.Release.Github
{
    public class GithubReleaseSource : ReleaseSourceBase
    {
        private static readonly Regex ReleaseFeaturesGroup = new Regex(@"\*\s+[0-9a-f]{40}\s+New:\s*(?<text>.*?)\r*$", RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ReleaseFixesGroup = new Regex(@"\*\s+[0-9a-f]{40}\s+Fixed:\s*(?<text>.*?)\r*$", RegexOptions.Compiled | RegexOptions.Multiline);

        private readonly Config _config;
        private readonly IUpdateService _updateService;
        private readonly IUpdateFileService _updateFileService;
        private readonly GitHubClient _gitHubClient;
        private readonly HttpClient _httpClient;

        public GithubReleaseSource(IUpdateService updateService,
                                   IUpdateFileService updateFileService,
                                   IOptions<Config> config)
        {
            _updateService = updateService;
            _updateFileService = updateFileService;
            _config = config.Value;
            _gitHubClient = new GitHubClient(new ProductHeaderValue("ServarrAPI"));
            _httpClient = new HttpClient();
        }

        protected override async Task<List<string>> DoFetchReleasesAsync()
        {
            var updated = new HashSet<string>();

            var githubOrg = _config.GithubOrg ?? _config.Project;
            var releases = (await _gitHubClient.Repository.Release.GetAll(githubOrg, _config.Project)).ToArray();
            var validReleases = releases
                .Where(r => r.TagName.StartsWith("v") && VersionUtil.IsValid(r.TagName.Substring(1)))
                .Take(3)
                .Reverse();

            foreach (var release in validReleases)
            {
                // Check if release has been published.
                if (!release.PublishedAt.HasValue)
                {
                    continue;
                }

                var version = release.TagName.Substring(1);

                // determine the branch
                var branch = release.Assets.Any(a => a.Name.StartsWith(string.Format("{0}.master", _config.Project))) ? "master" : "develop";

                if (await ProcessRelease(release, branch, version))
                {
                    updated.Add(branch);
                }

                // releases on master should also appear on develop
                if (branch == "master" && await ProcessRelease(release, "develop", version))
                {
                    updated.Add("develop");
                }
            }

            return updated.ToList();
        }

        private async Task<bool> ProcessRelease(Octokit.Release release, string branch, string version)
        {
            var isNewRelease = false;

            // Get an updateEntity
            var updateEntity = await _updateService.Find(version, branch).ConfigureAwait(false);

            if (updateEntity != null)
            {
                return isNewRelease;
            }

            var parsedVersion = Version.Parse(version);

            // Create update object
            updateEntity = new UpdateEntity
            {
                Version = version,
                IntVersion = parsedVersion.ToIntVersion(),
                ReleaseDate = release.PublishedAt.Value.UtcDateTime,
                Branch = branch
            };

            // Set new release to true.
            isNewRelease = true;

            // Parse changes
            var releaseBody = release.Body;

            var features = ReleaseFeaturesGroup.Matches(releaseBody);
            if (features.Any())
            {
                updateEntity.New.Clear();

                foreach (Match match in features)
                {
                    updateEntity.New.Add(match.Groups["text"].Value);
                }
            }

            var fixes = ReleaseFixesGroup.Matches(releaseBody);
            if (fixes.Any())
            {
                updateEntity.Fixed.Clear();

                foreach (Match match in fixes)
                {
                    updateEntity.Fixed.Add(match.Groups["text"].Value);
                }
            }

            await _updateService.Insert(updateEntity).ConfigureAwait(false);

            // Process release files.
            await Task.WhenAll(release.Assets.Select(x => ProcessAsset(x, branch, updateEntity.Id)));

            return isNewRelease;
        }

        private async Task ProcessAsset(Octokit.ReleaseAsset releaseAsset, string branch, int updateId)
        {
            var operatingSystem = Parser.ParseOS(releaseAsset.Name);
            if (!operatingSystem.HasValue)
            {
                return;
            }

            var runtime = Parser.ParseRuntime(releaseAsset.Name);
            var arch = Parser.ParseArchitecture(releaseAsset.Name);
            var installer = Parser.ParseInstaller(releaseAsset.Name);

            // Calculate the hash of the zip file.
            var releaseZip = Path.Combine(_config.DataDirectory, branch.ToString().ToLower(), releaseAsset.Name);
            string releaseHash;

            if (!File.Exists(releaseZip))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(releaseZip));

                using var fileStream = File.OpenWrite(releaseZip);
                using var artifactStream = await _httpClient.GetStreamAsync(releaseAsset.BrowserDownloadUrl);
                await artifactStream.CopyToAsync(fileStream);
            }

            using (var stream = File.OpenRead(releaseZip))
            using (var sha = SHA256.Create())
            {
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
                Filename = releaseAsset.Name,
                Url = releaseAsset.BrowserDownloadUrl,
                Hash = releaseHash,
                Installer = installer
            };

            await _updateFileService.Insert(updateFile).ConfigureAwait(false);
        }
    }
}
