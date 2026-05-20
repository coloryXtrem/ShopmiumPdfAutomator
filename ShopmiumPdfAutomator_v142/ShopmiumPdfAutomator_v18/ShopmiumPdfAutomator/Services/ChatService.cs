using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Chat GPT-4o avec historique, images (Vision) et contexte produit Shopmium.
    /// </summary>
    public class ChatService
    {
        private readonly HttpClient _http = new()
            { Timeout = TimeSpan.FromSeconds(90) };

        private readonly List<object> _history = [];

        public string Model { get; set; } = "gpt-4o";

        // Contexte produit injecté dans le système
        public string? ProductContext { get; set; }

        // Prompt système de base
        private string BuildSystemPrompt()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(
                "You are GPT-4o integrated in Shopmium PDF Automator, " +
                "a French cashback app assistant. " +
                "Always respond in the same language as the user (French preferred). " +
                "Be concise, practical and helpful.");

            if (!string.IsNullOrWhiteSpace(ProductContext))
            {
                sb.AppendLine();
                sb.AppendLine("=== CONTEXTE PRODUIT SHOPMIUM ===");
                sb.AppendLine(ProductContext);
                sb.AppendLine("=================================");
                sb.AppendLine("Use this product context to answer questions precisely.");
            }
            return sb.ToString();
        }

        // ── Envoyer texte simple ──────────────────────────────────────────────
        public async Task<string> SendAsync(string userMessage, string apiKey)
        {
            _history.Add(new { role = "user", content = userMessage });
            return await CallApiAsync(apiKey);
        }

        // ── Envoyer texte + image (Vision) ────────────────────────────────────
        public async Task<string> SendWithImageAsync(
            string userMessage, byte[] imageData, string apiKey)
        {
            var mime  = imageData.Length > 4 && imageData[0] == 0x89
                ? "image/png" : "image/jpeg";
            var b64   = Convert.ToBase64String(imageData);

            var content = new object[]
            {
                new { type = "text",      text = userMessage },
                new { type = "image_url", image_url = new
                {
                    url    = $"data:{mime};base64,{b64}",
                    detail = "high"
                }}
            };

            _history.Add(new { role = "user", content = content });
            return await CallApiAsync(apiKey);
        }

        // ── Appel API commun ──────────────────────────────────────────────────
        private async Task<string> CallApiAsync(string apiKey)
        {
            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt() }
            };
            messages.AddRange(_history);

            var body = new
            {
                model      = Model,
                max_tokens = 2000,
                messages   = messages.ToArray()
            };

            var req = new HttpRequestMessage(
                HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            var raw  = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _history.RemoveAt(_history.Count - 1);
                try
                {
                    using var err = JsonDocument.Parse(raw);
                    throw new Exception(err.RootElement
                        .GetProperty("error")
                        .GetProperty("message")
                        .GetString() ?? raw);
                }
                catch (JsonException) { throw new Exception(raw); }
            }

            using var doc = JsonDocument.Parse(raw);
            var answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            _history.Add(new { role = "assistant", content = answer });
            return answer;
        }

        public void ClearHistory()
        {
            _history.Clear();
            ProductContext = null;
        }

        public int MessageCount => _history.Count;
    }
}
