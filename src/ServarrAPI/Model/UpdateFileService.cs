using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServarrAPI.Model
{
    public interface IUpdateFileService
    {
        Task<UpdateFileEntity> Insert(UpdateFileEntity model);
        Task<UpdateFileEntity> Find(string version, string branch, OperatingSystem os, Runtime runtime, Architecture arch, bool installer);
        Task<List<UpdateFileEntity>> Find(string branch, OperatingSystem os, Runtime runtime, Architecture arch, bool installer, int count, string installedVersion = null, string runtimeVersion = null, bool excludeMajorVersions = false);
    }

    public class UpdateFileService : IUpdateFileService
    {
        private readonly ILogger _logger;
        private readonly IUpdateFileRepository _repo;
        private readonly Config _config;

        public UpdateFileService(IUpdateFileRepository repo,
                                 ILogger<IUpdateFileService> logger,
                                 IOptions<Config> optionsConfig)
        {
            _repo = repo;
            _logger = logger;
            _config = optionsConfig.Value;
        }

        public Task<UpdateFileEntity> Insert(UpdateFileEntity model)
        {
            return _repo.Insert(model);
        }

        public Task<UpdateFileEntity> Find(string version, string branch, OperatingSystem os, Runtime runtime, Architecture arch, bool installer)
        {
            runtime = SetRuntime(runtime);
            arch = SetArch(runtime, arch, os);
            os = SetOs(runtime, os);
            var mappedBranch = GetMappedBranch(branch);

            return _repo.Find(version, mappedBranch, os, runtime, arch, installer);
        }

        public Task<List<UpdateFileEntity>> Find(string branch, OperatingSystem os, Runtime runtime, Architecture arch, bool installer, int count, string installedVersion = null, string runtimeVersion = null, bool excludeMajorVersions = false)
        {
            runtime = SetRuntime(runtime);
            arch = SetArch(runtime, arch, os);
            os = SetOs(runtime, os);
            var maxVersion = GetMaxVersion(installedVersion, os, runtime, runtimeVersion, excludeMajorVersions);
            var mappedBranch = GetMappedBranch(branch);

            return _repo.Find(mappedBranch, os, runtime, arch, installer, count, maxVersion);
        }

        private Runtime SetRuntime(Runtime runtime)
        {
            // Mono and Dotnet are equivalent for our purposes
            if (runtime == Runtime.Mono)
            {
                return Runtime.DotNet;
            }

            return runtime;
        }

        private Architecture SetArch(Runtime runtime, Architecture arch, OperatingSystem os)
        {
            // If runtime is DotNet then default arch to x64 (or x86 for Windows given we build both)
            if (runtime == Runtime.DotNet)
            {
                if (os == OperatingSystem.Windows)
                {
                    return Architecture.X86;
                }

                return Architecture.X64;
            }

            return arch;
        }

        private OperatingSystem SetOs(Runtime runtime, OperatingSystem os)
        {
            // We only care about LinuxMusl and BSD for net core
            if (runtime == Runtime.DotNet &&
                (os == OperatingSystem.LinuxMusl ||
                 os == OperatingSystem.Bsd))
            {
                return OperatingSystem.Linux;
            }

            return os;
        }

        private Version GetMaxVersion(string currentVersion, OperatingSystem os, Runtime runtime, string runtimeVersion, bool excludeMajorVersions)
        {
            var bounds = new List<Version>();

            if (!string.IsNullOrWhiteSpace(currentVersion) &&
                Version.TryParse(currentVersion, out var installedVersion))
            {
                var maxVersion = _config.VersionGates.OrderBy(x => x.MaxVersion).FirstOrDefault(x => installedVersion <= x.MaxVersion)?.MaxUpgradeVersion;
                if (maxVersion != null)
                {
                    bounds.Add(maxVersion);
                }

                if (excludeMajorVersions)
                {
                    bounds.Add(new Version(installedVersion.Major, 99, 99, 999_999));
                }
            }

            // We override mono runtime to dotnet earlier, so check for dotnet and not windows
            if (runtime == Runtime.DotNet &&
                os != OperatingSystem.Windows &&
                !string.IsNullOrWhiteSpace(runtimeVersion) &&
                Version.TryParse(runtimeVersion, out var monoVersion))
            {
                var maxVersion = _config.MonoGates.OrderBy(x => x.MonoVersion).FirstOrDefault(x => monoVersion <= x.MonoVersion)?.MaxUpgradeVersion;
                if (maxVersion != null)
                {
                    bounds.Add(maxVersion);
                }
            }

            return bounds.Any() ? bounds.Min() : null;
        }

        private string GetMappedBranch(string branch)
        {
            var branchRedirects = _config.BranchRedirects;

            if (branchRedirects == null)
            {
                return branch;
            }

            if (branchRedirects.TryGetValue(branch.ToLowerInvariant(), out var mappedBranch))
            {
                return mappedBranch;
            }

            return branch;
        }
    }
}
