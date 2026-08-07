using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZkoolGraphQlClient
    {
        private readonly Uri _address;
        private readonly HttpClient _httpClient;

        public ZkoolGraphQlClient(Uri address, HttpClient httpClient)
        {
            _address = address;
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<JObject> SendAsync(string query, object variables = null, CancellationToken cancellationToken = default)
        {
            var serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _address)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { query, variables }, serializerSettings),
                    Encoding.UTF8,
                    "application/json")
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            var graphQlResponse = JsonConvert.DeserializeObject<GraphQlResponse>(rawBody);
            if (graphQlResponse?.Errors?.Any() is true)
            {
                throw new GraphQlApiException(graphQlResponse.Errors.Select(error => error.Message));
            }

            return graphQlResponse?.Data ?? new JObject();
        }

        internal class GraphQlResponse
        {
            [JsonProperty("data")] public JObject Data { get; set; }
            [JsonProperty("errors")] public List<GraphQlError> Errors { get; set; }
        }

        internal class GraphQlError
        {
            [JsonProperty("message")] public string Message { get; set; }
        }

        public class GraphQlApiException : Exception
        {
            public GraphQlApiException(IEnumerable<string> messages) : base(string.Join("; ", messages))
            {
            }
        }
    }
}
