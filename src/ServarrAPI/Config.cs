using System.Collections.Generic;
using ServarrAPI.Model;

namespace ServarrAPI
{
    public class Config
    {
        public Config()
        {
            VersionGates = new List<VersionGate>();
            MonoGates = new List<MonoGate>();
        }

        public string DataDirectory { get; set; }
        public string ApiKey { get; set; }
        public string AzureOrg { get; set; }
        public int AzurePipelineId { get; set; }
        public string GithubOrg { get; set; }
        public string Project { get; set; }
        public bool LogSql { get; set; }
        public List<VersionGate> VersionGates { get; set; }
        public List<MonoGate> MonoGates { get; set; }
        public Dictionary<string, string> BranchRedirects { get; set; }
    }
}
