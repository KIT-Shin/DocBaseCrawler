using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DocBaseCrawler
{
    public static class Function1
    {
        private static readonly string[] Authors;
        private static readonly string Domain;
        private static readonly string Id;
        private static readonly string Token;

        static Function1()
        {
            Authors = Environment.GetEnvironmentVariable("DOCBASE_CRAWLER_AUTHORS").Split(";");
            Domain = Environment.GetEnvironmentVariable("DOCBASE_CRAWLER_DOMAIN");
            Id = Environment.GetEnvironmentVariable("DOCBASE_CRAWLER_POSTID");
            Token = Environment.GetEnvironmentVariable("DOCBASE_CRAWLER_TOKEN");
            if (Domain == null || Id == null || Token == null) throw new NullReferenceException();
        }

        [FunctionName("ResetFunction")]
        public static async Task RestFunction([HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)]
            HttpRequest req)
        {
            var updateMemo = new StringBuilder();
            var (posts, needFirstDay) = await GetMemos(Authors, DateTime.MinValue, DateTime.MaxValue);
            if (posts.Count <= 0) return;
            updateMemo.AppendLine($"# {posts[0].created_at:yyyy/MM/dd}");

            DateTime before = posts[0].created_at;
            foreach (var post in posts)
            {
                if (DateHash(before) != DateHash(post.created_at))
                {
                    updateMemo.AppendLine($"# {post.created_at:yyyy/MM/dd}");
                    before = post.created_at;
                }

                updateMemo.AppendLine($"![]({post.user.profile_image_url} =50x)");
                updateMemo.AppendLine($"{post.user.name}");
                updateMemo.AppendLine($"#{{{post.id}}}");
            }

            await UpdateMemo(updateMemo.ToString());
        }

        [FunctionName("UpdateFunction")]
        public static async Task UpdateFunction([TimerTrigger("0 */15 * * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {myTimer.ScheduleStatus.LastUpdated} ");

            var currentMemo = await GetCurrentMemo();
            var updateMemo = new StringBuilder(currentMemo);
            var end = myTimer.ScheduleStatus.Last - TimeSpan.FromMinutes(5);
            var begin = end - TimeSpan.FromMinutes(15);
            var (posts, needFirstDay) = await GetMemos(Authors, begin, end);
            if (posts.Count <= 0) return;
            if (needFirstDay)
            {
                updateMemo.AppendLine($"# {posts[0].created_at:yyyy/MM/dd}");
            }

            DateTime before = posts[0].created_at;
            foreach (var post in posts)
            {
                if (DateHash(before) != DateHash(post.created_at))
                {
                    updateMemo.AppendLine($"# {post.created_at:yyyy/MM/dd}");
                    before = post.created_at;
                }

                updateMemo.AppendLine($"![]({post.user.profile_image_url} =50x)");
                updateMemo.AppendLine($"{post.user.name}");
                updateMemo.AppendLine($"#{{{post.id}}}");
            }

            await UpdateMemo(updateMemo.ToString());
        }

        private static int DateHash(DateTime d)
        {
            return d.Year * 367 + d.DayOfYear;
        }

        private static HttpRequestMessage CreateMessage()
        {
            var msg = new HttpRequestMessage();
            msg.Headers.Add("X-Api-Version", "1");
            msg.Headers.Add("X-DocBaseToken", Token);
            return msg;
        }

        private static async Task UpdateMemo(string body)
        {
            var client = new HttpClient();
            var msg = CreateMessage();
            msg.RequestUri = new Uri($"https://api.docbase.io/teams/{Domain}/posts/{Id}");
            msg.Method = HttpMethod.Patch;
            msg.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                body = body,
                notice = false,
                scope = "private"
            }), Encoding.UTF8, "application/json");

            await client.SendAsync(msg);
        }

        private static async Task<(IList<Post>, bool)> GetMemos(string[] authors, DateTime begin, DateTime end)
        {
            var builder = new StringBuilder("( ");
            for (int i = 0; i < authors.Length; i++)
            {
                builder.Append("author:");
                builder.Append(authors[i]);
                if (i != authors.Length - 1) builder.Append(" OR ");
            }

            builder.Append(" ) ");
            builder.Append($"created_at:{begin:yyyy-MM-dd}~{end:yyyy-MM-dd}");
            var param = new Dictionary<string, string>
            {
                {"q", builder.ToString()},
                {"per_page", "100"}
            };
            var msg = CreateMessage();
            msg.Method = HttpMethod.Get;
            string uri =
                $"https://api.docbase.io/teams/{Domain}/posts?{await new FormUrlEncodedContent(param).ReadAsStringAsync()}";
            var posts = new List<Post>();
            do
            {
                var postsResp = await GetPosts(uri);
                posts.AddRange(postsResp.posts);
                uri = postsResp.meta.next_paget;
            } while (uri != null);

            return (
                posts.OrderBy(val => val.created_at)
                    .Where(val => begin <= val.created_at && val.created_at < end)
                    .ToList(),
                posts.All(val => begin <= val.created_at)
            );
        }

        private static async Task<PostsResponse> GetPosts(string uri)
        {
            var client = new HttpClient();
            var msg = CreateMessage();
            msg.Method = HttpMethod.Get;
            msg.RequestUri =
                new Uri(uri);
            var resp = await client.SendAsync(msg);
            var respBody = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<PostsResponse>(respBody);
        }

        private static async Task<string> GetCurrentMemo()
        {
            using (var client = new HttpClient())
            {
                using (var getCurrentMemo = CreateMessage())
                {
                    getCurrentMemo.Method = HttpMethod.Get;
                    getCurrentMemo.RequestUri = new Uri($"https://api.docbase.io/teams/{Domain}/posts/{Id}");
                    using (var resp = await client.SendAsync(getCurrentMemo))
                    {
                        var currentMemoContent = await resp.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeAnonymousType(currentMemoContent, new {body = ""}).body;
                    }
                }
            }
        }
    }

    class Post
    {
        public int id { get; set; }
        public DateTime created_at { get; set; }
        public User user;
    }

    class User
    {
        public string name { get; set; }
        public string profile_image_url { get; set; }
    }

    class PostsResponse
    {
        public class Metadata
        {
            public string next_paget { get; set; }
            public int total { get; set; }
        }

        public List<Post> posts { get; set; }
        public Metadata meta { get; set; }
    }
}