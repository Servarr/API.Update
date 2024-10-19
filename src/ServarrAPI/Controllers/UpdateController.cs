using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfluxDB.Collector;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ServarrAPI.Model;
using ServarrAPI.Util;
using Architecture = System.Runtime.InteropServices.Architecture;
using OperatingSystem = ServarrAPI.Model.OperatingSystem;

namespace ServarrAPI.Controllers.Update
{
    [Route("[controller]")]
    public class UpdateController : Controller
    {
        private readonly IUpdateFileService _updateFileService;
        private readonly string _project;

        public UpdateController(IUpdateFileService updateFileService, IOptions<Config> config)
        {
            _updateFileService = updateFileService;
            _project = config.Value.Project;
        }

        [Route("{branch}/changes")]
        [HttpGet]
        public async Task<object> GetChanges([FromRoute(Name = "branch")] string updateBranch,
                                             [FromQuery(Name = "version")] string urlVersion,
                                             [FromQuery(Name = "os")] OperatingSystem operatingSystem,
                                             [FromQuery(Name = "runtimeVer")] string urlRuntimeVersion,
                                             [FromQuery(Name = "runtime")] Runtime runtime = Runtime.DotNet,
                                             [FromQuery(Name = "arch")] Architecture arch = Architecture.X64)
        {
            Response.Headers[HeaderNames.CacheControl] = GetCacheControlHeader(DateTime.UtcNow.AddMinutes(5));

            var updateFiles = await _updateFileService.Find(updateBranch, operatingSystem, runtime, arch, false, 5, urlVersion, urlRuntimeVersion);

            var response = new List<UpdatePackage>();

            foreach (var updateFile in updateFiles)
            {
                var update = updateFile.Update.Value;
                UpdateChanges updateChanges = null;

                if (update.New.Count != 0 || update.Fixed.Count != 0)
                {
                    updateChanges = new UpdateChanges
                    {
                        New = update.New,
                        Fixed = update.Fixed
                    };
                }

                response.Add(new UpdatePackage
                {
                    Version = update.Version,
                    ReleaseDate = update.ReleaseDate,
                    Filename = updateFile.Filename,
                    Url = updateFile.Url,
                    Changes = updateChanges,
                    Hash = updateFile.Hash,
                    Branch = update.Branch.ToString().ToLower()
                });
            }

            return response;
        }

        [Route("{branch}")]
        [HttpGet]
        public async Task<object> GetUpdates([FromRoute(Name = "branch")] string updateBranch,
                                             [FromQuery(Name = "version")] string urlVersion,
                                             [FromQuery(Name = "os")] OperatingSystem operatingSystem,
                                             [FromQuery(Name = "runtime")] Runtime runtime,
                                             [FromQuery(Name = "runtimeVer")] string urlRuntimeVersion,
                                             [FromQuery(Name = "arch")] Architecture arch,
                                             [FromQuery(Name = "includeMajorVersion")] bool includeMajorVersion = false,
                                             [FromQuery(Name = "dbType")] string dbType = "SQLite",
                                             [FromQuery(Name = "active")] bool activeInstall = true)
        {
            Response.Headers[HeaderNames.CacheControl] = GetCacheControlHeader(DateTime.UtcNow);

            // Check given version
            if (!Version.TryParse(urlVersion, out var version))
            {
                return new
                {
                    ErrorMessage = "Invalid version number specified."
                };
            }

            var userAgentInfo = new UserAgentInfo(Request.Headers["User-Agent"].ToString());

            // Dont' send metrics for dev/debug instances
            if (version.Major < 10)
            {
                var remoteIpAddress = Request.HttpContext.Connection.RemoteIpAddress;

                Metrics.Write("userstats",
                    new Dictionary<string, object>
                    {
                        { "source", remoteIpAddress }
                    },
                    new Dictionary<string, string>
                    {
                        { "program", _project },
                        { "requestApp", userAgentInfo.App ?? "Unknown" },
                        { "branch", updateBranch },
                        { "version", urlVersion },
                        { "os", operatingSystem.ToString() },
                        { "osName", userAgentInfo.Name ?? "Unknown" },
                        { "osVersion", userAgentInfo.OsVersion ?? "Unknown" },
                        { "runtime", runtime.ToString() },
                        { "runtimeVersion", urlRuntimeVersion },
                        { "arch", arch.ToString() },
                        { "activeinstall", activeInstall.ToString() },
                        { "dbType", dbType }
                    });
            }

            var files = await _updateFileService.Find(updateBranch, operatingSystem, runtime, arch, false, 1, urlVersion, urlRuntimeVersion, !includeMajorVersion);

            var updateFile = files.FirstOrDefault();

            if (updateFile == null)
            {
                return new UpdatePackageContainer
                {
                    Available = false
                };
            }

            var update = updateFile.Update.Value;

            // Compare given version and update version
            var updateVersion = new Version(update.Version);
            if (updateVersion.CompareTo(version) <= 0)
            {
                return new UpdatePackageContainer
                {
                    Available = false
                };
            }

            // Get the update changes
            UpdateChanges updateChanges = null;

            if (update.New.Count != 0 || update.Fixed.Count != 0)
            {
                updateChanges = new UpdateChanges
                {
                    New = update.New,
                    Fixed = update.Fixed
                };
            }

            return new UpdatePackageContainer
            {
                Available = true,
                UpdatePackage = new UpdatePackage
                {
                    Version = update.Version,
                    ReleaseDate = update.ReleaseDate,
                    Filename = updateFile.Filename,
                    Url = updateFile.Url,
                    Changes = updateChanges,
                    Hash = updateFile.Hash,
                    Branch = update.Branch.ToString().ToLower(),
                    Runtime = updateFile.Runtime.ToString().ToLower()
                }
            };
        }

        [Route("{branch}/updatefile")]
        [HttpGet]
        public async Task<object> GetUpdateFile([FromRoute(Name = "branch")] string updateBranch,
                                                [FromQuery(Name = "version")] string urlVersion,
                                                [FromQuery(Name = "os")] OperatingSystem operatingSystem,
                                                [FromQuery(Name = "runtime")] Runtime runtime,
                                                [FromQuery(Name = "arch")] Architecture arch,
                                                [FromQuery(Name = "installer")] bool installer = false)
        {
            Response.Headers[HeaderNames.CacheControl] = GetCacheControlHeader(DateTime.UtcNow);

            UpdateFileEntity updateFile;

            if (urlVersion != null)
            {
                if (!Version.TryParse(urlVersion, out var _))
                {
                    return new
                    {
                        ErrorMessage = "Invalid version number specified."
                    };
                }

                updateFile = await _updateFileService.Find(urlVersion, updateBranch, operatingSystem, runtime, arch, installer);
            }
            else
            {
                var updateFiles = await _updateFileService.Find(updateBranch, operatingSystem, runtime, arch, installer, 1);
                updateFile = updateFiles.FirstOrDefault();
            }

            if (updateFile == null)
            {
                return new
                {
                    ErrorMessage = $"Update file for {updateBranch}-{urlVersion} not found."
                };
            }

            return RedirectPermanent(updateFile.Url);
        }

        private string GetCacheControlHeader(DateTime expiry)
        {
            var now = DateTime.UtcNow;
            var maxage = (int)(expiry - now).TotalSeconds;

            return $"public,s-maxage={maxage},max-age=0";
        }
    }
}
