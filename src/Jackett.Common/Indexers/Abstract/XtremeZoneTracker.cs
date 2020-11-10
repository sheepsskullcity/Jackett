using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Abstract
{
    [ExcludeFromCodeCoverage]
    public abstract class XtremeZoneTracker : BaseWebIndexer
    {
        private readonly Dictionary<string, string> ApiHeaders = new Dictionary<string, string>
        {
            {"Accept", "application/json"},
            {"Content-Type", "application/json"}
        };
        private string LoginUrl => SiteLink + "api/login";
        private string SearchUrl => SiteLink + "api/torrent";
        private string _token;

        private new ConfigurationDataBasicLogin configData => (ConfigurationDataBasicLogin)base.configData;

        protected XtremeZoneTracker(string link, string id, string name, string description,
                                    IIndexerConfigurationService configService, WebClient client, Logger logger,
                                    IProtectionService p, TorznabCapabilities caps)
            : base(id: id,
                   name: name,
                   description: description,
                   link: link,
                   caps: caps,
                   configService: configService,
                   client: client,
                   logger: logger,
                   p: p,
                   configData: new ConfigurationDataBasicLogin())
        {
            Encoding = Encoding.UTF8;
            Language = "ro-ro";
            Type = "private";

            // requestDelay for API Limit (1 request per 2 seconds)
            webclient.requestDelay = 2.1;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            await RenewalTokenAsync();

            var releases = await PerformQuery(new TorznabQuery());
            await ConfigureIfOK(string.Empty, releases.Any(),
                                () => throw new Exception("Could not find releases."));

            return IndexerConfigurationStatus.Completed;
        }

        private async Task RenewalTokenAsync()
        {
            var body = new Dictionary<string, string>
            {
                { "username", configData.Username.Value.Trim() },
                { "password", configData.Password.Value.Trim() }
            };
            var jsonData = JsonConvert.SerializeObject(body);
            var result = await RequestWithCookiesAsync(
                LoginUrl, method: RequestType.POST, headers: ApiHeaders, rawbody: jsonData);
            var json = JObject.Parse(result.ContentString);
            _token = json.Value<string>("token");
            if (_token == null)
                throw new Exception(json.Value<string>("message"));
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            //var categoryMapping = MapTorznabCapsToTrackers(query).Distinct().ToList();
            var qc = new List<KeyValuePair<string, string>> // NameValueCollection don't support cat[]=19&cat[]=6
            {
                {"itemsPerPage", "100"},
                {"sort", "torrent.createdAt"},
                {"direction", "desc"}
            };

            foreach (var cat in MapTorznabCapsToTrackers(query))
                qc.Add("categories[]", cat);

            if (query.IsImdbQuery)
                qc.Add("imdbId", query.ImdbID);
            else
                qc.Add("search", query.GetQueryString());

            if (string.IsNullOrWhiteSpace(_token)) // fist time login
                await RenewalTokenAsync();

            var searchUrl = SearchUrl + "?" + qc.GetQueryString();
            var response = await RequestWithCookiesAsync(searchUrl, headers: GetSearchHeaders());
            if (response.Status == HttpStatusCode.Unauthorized)
            {
                await RenewalTokenAsync(); // re-login
                response = await RequestWithCookiesAsync(searchUrl, headers: GetSearchHeaders());
            }
            else if (response.Status != HttpStatusCode.OK)
                throw new Exception($"Unknown error in search: {response.ContentString}");

            try
            {
                var rows = JArray.Parse(response.ContentString);
                foreach (var row in rows)
                {
                    var id = row.Value<string>("id");
                    var details = new Uri($"{SiteLink}browse/{id}");
                    var link = new Uri($"{SiteLink}api/torrent/{id}/download");
                    var publishDate = DateTime.Parse(row.Value<string>("created_at"), CultureInfo.InvariantCulture);
                    var cat = row.Value<JToken>("category").Value<string>("id");

                    // "description" field in API has too much HTML code
                    var description = row.Value<string>("short_description");

                    var posterStr = row.Value<string>("poster");
                    var poster = Uri.TryCreate(posterStr, UriKind.Absolute, out var posterUri) ? posterUri : null;

                    var dlVolumeFactor = row.Value<bool>("is_half_download") ? 0.5: 1.0;
                    dlVolumeFactor = row.Value<bool>("is_freeleech") ? 0.0 : dlVolumeFactor;
                    var ulVolumeFactor = row.Value<bool>("is_double_upload") ? 2.0: 1.0;

                    var release = new ReleaseInfo
                    {
                        Title = row.Value<string>("name"),
                        Link = link,
                        Details = details,
                        Guid = details,
                        Category =  MapTrackerCatToNewznab(cat),
                        PublishDate = publishDate,
                        Description = description,
                        Poster = poster,
                        Size = row.Value<long>("size"),
                        Grabs = row.Value<long>("times_completed"),
                        Seeders = row.Value<int>("seeders"),
                        Peers = row.Value<int>("leechers") + row.Value<int>("seeders"),
                        DownloadVolumeFactor = dlVolumeFactor,
                        UploadVolumeFactor = ulVolumeFactor,
                        MinimumRatio = 1,
                        MinimumSeedTime = 172800 // 48 hours
                    };

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.ContentString, ex);
            }
            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var response = await RequestWithCookiesAsync(link.ToString(), headers: GetSearchHeaders());
            if (response.Status == HttpStatusCode.Unauthorized)
            {
                await RenewalTokenAsync();
                response = await RequestWithCookiesAsync(link.ToString(), headers: GetSearchHeaders());
            }
            else if (response.Status != HttpStatusCode.OK)
                throw new Exception($"Unknown error in download: {response.ContentBytes}");
            return response.ContentBytes;
        }

        private Dictionary<string, string> GetSearchHeaders() => new Dictionary<string, string>
        {
            {"Authorization", $"Bearer {_token}"}
        };
    }
}
