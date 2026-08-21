using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZkoolGraphQlClient : IDisposable
    {
        private readonly GraphQLHttpClient _client;

        public ZkoolGraphQlClient(Uri httpEndpoint, HttpClient httpClient)
        {
            var wsEndpoint = new UriBuilder(httpEndpoint)
            {
                Scheme = httpEndpoint.Scheme == "https" ? "wss" : "ws",
                Path = "/subscriptions"
            }.Uri;

            var options = new GraphQLHttpClientOptions
            {
                EndPoint = httpEndpoint,
                WebSocketEndPoint = wsEndpoint,
                WebSocketProtocol = "graphql-transport-ws"
            };

            var serializer = new NewtonsoftJsonSerializer(new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            });

            _client = new GraphQLHttpClient(options, serializer, httpClient);
        }

        public async Task<JObject> SendAsync(string query, object variables = null, CancellationToken cancellationToken = default)
        {
            var request = new GraphQLRequest { Query = query, Variables = variables };
                // Console.WriteLine("=== GraphQL Request ===");
                // Console.WriteLine($"Query: {query}");
                // Console.WriteLine($"Variables: {JsonConvert.SerializeObject(variables)}");

                var response = await _client.SendQueryAsync<JObject>(request, cancellationToken);

                // Console.WriteLine("=== GraphQL Response ===");
                // Console.WriteLine($"Data: {response.Data?.ToString(Formatting.Indented)}");
            // var response = await _client.SendQueryAsync<JObject>(request, cancellationToken);

            if (response.Errors?.Any() is true)
                throw new GraphQlApiException(response.Errors.Select(e => e.Message));

            return response.Data ?? new JObject();
        }

        public IObservable<GraphQLResponse<JObject>> CreateSubscriptionStream(GraphQLRequest request, Action<Exception> webSocketExceptionHandler)
            => _client.CreateSubscriptionStream<JObject>(request, webSocketExceptionHandler);

        public void Dispose() => _client.Dispose();

        public class GraphQlApiException : Exception
        {
            public GraphQlApiException(IEnumerable<string> messages) : base(string.Join("; ", messages)) { }
        }
    }
}
