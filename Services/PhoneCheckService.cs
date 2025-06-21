using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace CheckScam.Services
{
    public class PhoneCheckService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public PhoneCheckService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<(bool IsValidNumverify, string LineType, string Carrier, bool IsSuspiciousVeriphone)> CheckPhoneAsync(string phoneNumber)
        {
            var client = _httpClientFactory.CreateClient();
            var numverifyKey = _configuration["ApiKeys:NumverifyApiKey"];
            var veriphoneKey = _configuration["ApiKeys:VeriphoneApiKey"];

            if (string.IsNullOrEmpty(numverifyKey) || string.IsNullOrEmpty(veriphoneKey))
            {
                System.Diagnostics.Debug.WriteLine("API key không được tìm thấy trong cấu hình.");
                return (false, "Unknown", "Unknown", false);
            }

            // Chuẩn hóa số điện thoại
            string normalizedPhone = NormalizePhoneNumber(phoneNumber);
            System.Diagnostics.Debug.WriteLine($"Original phone: {phoneNumber}, Normalized: {normalizedPhone}");

            bool isValidNumverify = false;
            string lineType = "Unknown";
            string carrier = "Unknown";
            bool isSuspicious = false;

            // Gọi Numverify API
            try
            {
                string numverifyUrl = $"http://apilayer.net/api/validate?access_key={numverifyKey}&number={normalizedPhone}&format=1";
                System.Diagnostics.Debug.WriteLine($"Numverify URL: {numverifyUrl}");

                var numverifyResponse = await client.GetAsync(numverifyUrl);
                var numverifyContent = await numverifyResponse.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Numverify Response: {numverifyContent}");

                if (!string.IsNullOrEmpty(numverifyContent))
                {
                    var numverifyData = JsonSerializer.Deserialize<JsonElement>(numverifyContent);

                    // Kiểm tra error từ API
                    if (numverifyData.TryGetProperty("error", out var errorElement))
                    {
                        var errorCode = errorElement.GetProperty("code").GetString();
                        var errorInfo = errorElement.GetProperty("info").GetString();
                        System.Diagnostics.Debug.WriteLine($"Numverify Error: {errorCode} - {errorInfo}");
                    }
                    else
                    {
                        isValidNumverify = numverifyData.TryGetProperty("valid", out var validElement) && validElement.GetBoolean();
                        lineType = numverifyData.TryGetProperty("line_type", out var lineElement) ? lineElement.GetString() ?? "Unknown" : "Unknown";
                        carrier = numverifyData.TryGetProperty("carrier", out var carrierElement) ? carrierElement.GetString() ?? "Unknown" : "Unknown";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Numverify API: {ex.Message}");
            }

            // Gọi Veriphone API
            try
            {
                string veriphoneUrl = $"https://api.veriphone.io/v2/verify?key={veriphoneKey}&phone={normalizedPhone}";
                System.Diagnostics.Debug.WriteLine($"Veriphone URL: {veriphoneUrl}");

                var veriphoneResponse = await client.GetAsync(veriphoneUrl);
                var veriphoneContent = await veriphoneResponse.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Veriphone Response: {veriphoneContent}");

                if (!string.IsNullOrEmpty(veriphoneContent))
                {
                    var veriphoneData = JsonSerializer.Deserialize<JsonElement>(veriphoneContent);

                    if (veriphoneData.TryGetProperty("status", out var statusElement) &&
                        statusElement.GetString() == "success")
                    {
                        // Cập nhật thông tin từ Veriphone nếu Numverify không có
                        if (carrier == "Unknown" && veriphoneData.TryGetProperty("carrier", out var vCarrierElement))
                        {
                            carrier = vCarrierElement.GetString() ?? "Unknown";
                        }

                        if (lineType == "Unknown" && veriphoneData.TryGetProperty("phone_type", out var phoneTypeElement))
                        {
                            lineType = phoneTypeElement.GetString() ?? "Unknown";
                        }

                        // Kiểm tra risk factors từ Veriphone
                        if (veriphoneData.TryGetProperty("risk_level", out var riskElement))
                        {
                            var riskLevel = riskElement.GetString();
                            isSuspicious = riskLevel == "high" || riskLevel == "medium";
                        }

                        // Kiểm tra các dấu hiệu nghi ngờ khác
                        if (veriphoneData.TryGetProperty("is_valid", out var isValidElement))
                        {
                            bool veriphoneValid = isValidElement.GetBoolean();
                            if (!isValidNumverify && veriphoneValid)
                            {
                                isValidNumverify = true; // Ưu tiên Veriphone nếu Numverify fail
                            }
                        }

                        // Kiểm tra loại số đặc biệt
                        if (lineType.ToLower().Contains("voip") || lineType.ToLower().Contains("virtual"))
                        {
                            isSuspicious = true;
                        }
                    }
                    else if (veriphoneData.TryGetProperty("error", out var vErrorElement))
                    {
                        System.Diagnostics.Debug.WriteLine($"Veriphone Error: {vErrorElement.GetString()}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Veriphone API: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"Final Result - Valid: {isValidNumverify}, LineType: {lineType}, Carrier: {carrier}, Suspicious: {isSuspicious}");
            return (isValidNumverify, lineType, carrier, isSuspicious);
        }

        private string NormalizePhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return phoneNumber;

            // Loại bỏ tất cả ký tự không phải số, trừ dấu +
            string cleaned = Regex.Replace(phoneNumber, @"[^\d+]", "");

            // Nếu số bắt đầu bằng 0 và có 9-10 số (VN format)
            if (cleaned.StartsWith("0") && (cleaned.Length == 9 || cleaned.Length == 10))
            {
                return "+84" + cleaned.Substring(1);
            }

            // Nếu số bắt đầu bằng 84 (không có +)
            if (cleaned.StartsWith("84") && cleaned.Length >= 10)
            {
                return "+" + cleaned;
            }

            // Nếu đã có + thì giữ nguyên
            if (cleaned.StartsWith("+"))
            {
                return cleaned;
            }

            // Nếu là số 9-10 chữ số không có mã vùng
            if (cleaned.Length >= 9 && cleaned.Length <= 10)
            {
                return "+84" + cleaned;
            }

            return cleaned;
        }
    }
}