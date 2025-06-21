using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using CheckScam.Models;
using CheckScam.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;
using System.IO;
using CheckScam.Services;

namespace CheckScam.Controllers
{
    public class Gr1Controller : Controller
    {
        private readonly CheckScamDbContext _context;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly IHttpClientFactory _httpClientFactory;

        public Gr1Controller(CheckScamDbContext context, UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _userManager = userManager;
            _signInManager = signInManager;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IActionResult> Index(int? page, string q)
        {
            var scamPost = new ScamPost(); // Khởi tạo để kiểm tra
            var apiResult = new { IsValidNumverify = false, LineType = "Unknown", Carrier = "Unknown", IsSuspicious = false, RiskLevel = "Low" };
            bool hasApiData = false;

            if (!string.IsNullOrEmpty(q))
            {
                // Chuẩn hóa số tối thiểu
                string rawQuery = q.Trim();
                string normalizedQuery = rawQuery;
                if (rawQuery.StartsWith("0") && (rawQuery.Length == 9 || rawQuery.Length == 10) && !rawQuery.StartsWith("+"))
                {
                    normalizedQuery = "+84" + rawQuery.Substring(1); // Chuyển 0972009161 thành +84972009161
                }
                System.Diagnostics.Debug.WriteLine($"Raw Query: {rawQuery}, Normalized Query: {normalizedQuery}");

                // Kiểm tra số trong database
                scamPost = await _context.ScamPosts
                    .FirstOrDefaultAsync(p => p.SdtScam == rawQuery && p.Status == "approved");

                if (scamPost == null)
                {
                    // Nếu không tìm thấy trong database, gọi API
                    var phoneService = HttpContext.RequestServices.GetService<PhoneCheckService>();
                    if (phoneService != null)
                    {
                        try
                        {
                            var (isValidNumverify, lineType, carrier, isSuspicious) = await phoneService.CheckPhoneAsync(normalizedQuery);
                            string riskLevel = "Low"; // Mặc định

                            // Đánh giá rủi ro dựa trên dấu hiệu gián tiếp
                            if (!isValidNumverify || carrier == "Unknown" || lineType == "voip")
                            {
                                riskLevel = "Medium"; // Nghi ngờ nếu không hợp lệ, nhà mạng không rõ, hoặc VoIP
                            }
                            if (isSuspicious)
                            {
                                riskLevel = "High"; // Cao nếu Veriphone ghi nhận rủi ro
                            }

                            // Kiểm tra với Gemini API
                            var isScamFromGemini = await CheckWithGeminiAsync(normalizedQuery);
                            if (isScamFromGemini)
                            {
                                riskLevel = "High"; // Nâng mức rủi ro nếu Gemini xác nhận
                            }

                            apiResult = new { IsValidNumverify = isValidNumverify, LineType = lineType, Carrier = carrier, IsSuspicious = isSuspicious, RiskLevel = riskLevel };
                            hasApiData = true;

                            // Thêm vào database nếu hợp lệ
                            if (isValidNumverify)
                            {
                                scamPost = new ScamPost
                                {
                                    NameScam = $"Kiểm tra số {q}",
                                    SdtScam = q,
                                    NoiDung = $"Thông tin từ API: {lineType}, {carrier}, Rủi ro: {riskLevel}",
                                    Status = "approved",
                                    LineType = lineType,
                                    Carrier = carrier,
                                    CreatedAt = DateTime.Now
                                };
                                _context.ScamPosts.Add(scamPost);
                                await _context.SaveChangesAsync();
                            }
                            else
                            {
                                scamPost = null; // Không hiển thị nếu không hợp lệ
                            }

                            // Thêm cảnh báo nếu rủi ro Medium hoặc High
                            if (riskLevel == "Medium" || riskLevel == "High")
                            {
                                ViewBag.Warning = "⚠️ Cảnh báo: Số này có thể tiềm ẩn rủi ro lừa đảo. Hãy cẩn thận và báo cáo nếu nghi ngờ!";
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Lỗi API: {ex.Message} - Query: {normalizedQuery}");
                        }
                    }
                }
                else
                {
                    // Nếu tìm thấy trong database, sử dụng dữ liệu từ database
                    apiResult = new
                    {
                        IsValidNumverify = true,
                        LineType = scamPost.LineType,
                        Carrier = scamPost.Carrier,
                        IsSuspicious = scamPost.NoiDung.Contains("Nghi ngờ lừa đảo"),
                        RiskLevel = scamPost.NoiDung.Contains("Rủi ro: High") ? "High" : scamPost.NoiDung.Contains("Rủi ro: Medium") ? "Medium" : "Low"
                    };
                    hasApiData = true;
                }
            }

            var paginatedPosts = scamPost != null ? new List<ScamPost> { scamPost } : new List<ScamPost>();

            int pageSize = 1; // Chỉ hiển thị 1 kết quả
            int pageNumber = page ?? 1;
            var displayedPosts = paginatedPosts
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.PageNumber = pageNumber;
            ViewBag.TotalPages = (int)Math.Ceiling((double)paginatedPosts.Count / pageSize);
            ViewBag.ApiResult = apiResult;
            ViewBag.HasApiData = hasApiData;
            return View(displayedPosts);
        }

        private async Task<bool> CheckWithGeminiAsync(string phoneNumber)
        {
            var client = _httpClientFactory.CreateClient();
            var prompt = new
            {
                prompt = $"Kiểm tra xem số điện thoại {phoneNumber} có phải là lừa đảo dựa trên dữ liệu từ các trang báo như https://vnexpress.net, https://tuoitre.vn, https://thanhnien.vn, https://dantri.com.vn, https://vietnamnet.vn, https://zingnews.vn, https://nhandan.vn, https://laodong.vn, https://kenh14.vn, https://plo.vn. Chỉ trả về true nếu có bằng chứng, false nếu không."
            };
            var content = new StringContent(JsonSerializer.Serialize(prompt), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.gemini.ai/v1/generate", content);
            var responseString = await response.Content.ReadAsStringAsync();
            var geminiData = JsonSerializer.Deserialize<Dictionary<string, object>>(responseString);

            return geminiData != null && geminiData.ContainsKey("result") && geminiData["result"].ToString().ToLower() == "true";
        }

        public IActionResult Login()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Login(string username, string password)
        {
            var result = await _signInManager.PasswordSignInAsync(username, password, false, false);
            if (result.Succeeded)
            {
                return RedirectToAction("Index");
            }
            ViewBag.Error = "Sai tài khoản hoặc mật khẩu!";
            return View();
        }

        public IActionResult Register()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Register(string username, string email, string password1, string password2)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password1) || string.IsNullOrEmpty(password2))
            {
                TempData["Error"] = "❌ Vui lòng điền đầy đủ thông tin!";
                return RedirectToAction("Register");
            }

            if (password1 != password2)
            {
                TempData["Error"] = "❌ Mật khẩu không khớp!";
                return RedirectToAction("Register");
            }

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                TempData["Error"] = "❌ Email không hợp lệ!";
                return RedirectToAction("Register");
            }

            var existingUser = await _userManager.FindByNameAsync(username);
            if (existingUser != null)
            {
                TempData["Error"] = "❌ Tên tài khoản đã tồn tại!";
                return RedirectToAction("Register");
            }

            existingUser = await _userManager.FindByEmailAsync(email);
            if (existingUser != null)
            {
                TempData["Error"] = "❌ Email đã được sử dụng!";
                return RedirectToAction("Register");
            }

            var user = new IdentityUser { UserName = username, Email = email };
            try
            {
                var result = await _userManager.CreateAsync(user, password1);
                if (result.Succeeded)
                {
                    if (username.ToLower() == "admin")
                    {
                        await _userManager.AddToRoleAsync(user, "Superuser");
                    }
                    await _signInManager.SignInAsync(user, isPersistent: false);
                    TempData["Success"] = "✅ Đăng ký thành công!";
                    return RedirectToAction("Index");
                }

                TempData["Error"] = string.Join("; ", result.Errors.Select(e => e.Description));
                return RedirectToAction("Register");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"❌ Lỗi hệ thống: {ex.Message}";
                return RedirectToAction("Register");
            }
        }

        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Index");
        }

        public async Task<IActionResult> ScamDetail(int id)
        {
            var scam = await _context.ScamPosts
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (scam == null)
            {
                return NotFound();
            }
            return View(scam);
        }

        [Authorize]
        public IActionResult Report()
        {
            return View();
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Report(PostScamDto model, List<IFormFile> images, [FromServices] PhoneCheckService phoneService)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "❌ Dữ liệu không hợp lệ!";
                return View(model);
            }

            // Kiểm tra số điện thoại với Numverify và Veriphone
            string lineType = null;
            string carrier = null;
            if (!string.IsNullOrEmpty(model.SdtScam))
            {
                var (isValidNumverify, lt, cr, isSuspicious) = await phoneService.CheckPhoneAsync(model.SdtScam);
                if (!isValidNumverify)
                {
                    TempData["Error"] = "❌ Số điện thoại không hợp lệ!";
                    return View(model);
                }
                lineType = lt;
                carrier = cr;
                if (isSuspicious)
                {
                    TempData["Warning"] = "⚠️ Số này được ghi nhận có nguy cơ lừa đảo!";
                }
                else if (lineType == "voip")
                {
                    TempData["Warning"] = "⚠️ Số VoIP có nguy cơ lừa đảo cao!";
                }
            }

            var scamPost = new ScamPost
            {
                NameScam = model.NameScam,
                StkScam = model.StkScam,
                SdtScam = model.SdtScam,
                NoiDung = model.NoiDung,
                Status = "pending",
                LineType = lineType,
                Carrier = carrier
            };
            _context.ScamPosts.Add(scamPost);
            await _context.SaveChangesAsync();

            foreach (var image in images)
            {
                if (image != null && image.Length > 0)
                {
                    var fileName = Guid.NewGuid().ToString() + Path.GetExtension(image.FileName);
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "media", "scam_images", fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await image.CopyToAsync(stream);
                    }
                    _context.ScamImages.Add(new ScamImage
                    {
                        ScamPostId = scamPost.Id,
                        ImagePath = $"/media/scam_images/{fileName}"
                    });
                }
            }
            await _context.SaveChangesAsync();
            TempData["Success"] = "✅ Tố cáo đã được gửi và đang chờ duyệt!";
            return RedirectToAction("Index");
        }

        [Authorize(Roles = "Superuser")]
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var scam = await _context.ScamPosts
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (scam == null)
            {
                TempData["Error"] = $"❌ Không tìm thấy bài tố cáo!";
                return RedirectToAction("Index");
            }

            try
            {
                // Xóa file hình ảnh vật lý
                foreach (var image in scam.Images)
                {
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", image.ImagePath.TrimStart('/'));
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }

                // Xóa bài và hình ảnh trong database
                _context.ScamImages.RemoveRange(scam.Images);
                _context.ScamPosts.Remove(scam);
                await _context.SaveChangesAsync();

                TempData["Success"] = $"🗑️ Đã xóa bài tố cáo: {scam.NameScam}";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"❌ Lỗi khi xóa bài: {ex.Message}";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Route("fetch-gemini-scams")]
        public async Task<IActionResult> FetchGeminiScams()
        {
            var prompt = @"{
                ""prompt"": ""Hãy trả về JSON với danh sách 3 vụ lừa đảo phổ biến. Dữ liệu phải là JSON hợp lệ với format: [
                    {""name"": ""Tên lừa đảo"", ""bank_account"": ""Số tài khoản"", ""phone_number"": ""Số điện thoại"", ""description"": ""Chi tiết vụ lừa đảo""},
                    {""name"": ""..."", ""bank_account"": ""..."", ""phone_number"": ""..."", ""description"": ""...""},
                    {""name"": ""..."", ""bank_account"": ""..."", ""phone_number"": ""..."", ""description"": ""...""}
                ] Chỉ trả về JSON, không kèm theo văn bản giải thích khác. Lưu ý: lấy thông tin thật, không phải ví dụ, hãy lấy các bài có số điện thoại hoặc số tài khoản ngân hàng (1 trong 2 hoặc cả 2), các bài không có 1 trong 2 thứ đó không lấy, lấy từ các trang báo như https://vnexpress.net, https://tuoitre.vn, https://thanhnien.vn, https://dantri.com.vn, https://vietnamnet.vn, https://zingnews.vn, https://nhandan.vn, https://laodong.vn, https://kenh14.vn, https://plo.vn.""
            }";

            try
            {
                var client = _httpClientFactory.CreateClient();
                var content = new StringContent(prompt, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.gemini.ai/v1/generate", content);
                var responseString = await response.Content.ReadAsStringAsync();
                var scamData = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(responseString);

                foreach (var item in scamData)
                {
                    var sdtScam = Regex.Replace(item["phone_number"] ?? "", @"\D", "");
                    sdtScam = string.IsNullOrEmpty(sdtScam) || sdtScam.ToLower() == "không có" ? null : sdtScam;
                    var existingPost = await _context.ScamPosts
                        .FirstOrDefaultAsync(p => p.NameScam == item["name"]);
                    if (existingPost != null)
                    {
                        existingPost.StkScam = item["bank_account"];
                        existingPost.SdtScam = sdtScam;
                        existingPost.NoiDung = item["description"];
                        existingPost.Status = "approved";
                    }
                    else
                    {
                        _context.ScamPosts.Add(new ScamPost
                        {
                            NameScam = item["name"],
                            StkScam = item["bank_account"],
                            SdtScam = sdtScam,
                            NoiDung = item["description"],
                            Status = "approved"
                        });
                    }
                }
                await _context.SaveChangesAsync();
                return Json(new { message = "Dữ liệu lừa đảo Gemini đã được thêm vào!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        [Route("api/scamposts")]
        public async Task<IActionResult> GetAllScamPosts()
        {
            var scamPosts = await _context.ScamPosts
                .Select(p => new GetAllPostDto
                {
                    Id = p.Id,
                    NameScam = p.NameScam,
                    StkScam = p.StkScam,
                    SdtScam = p.SdtScam,
                    NoiDung = p.NoiDung,
                    Status = p.Status
                })
                .ToListAsync();
            return Ok(scamPosts);
        }

        [HttpPost]
        [Route("api/scamposts")]
        public async Task<IActionResult> PostScamPost([FromBody] PostScamDto model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { error = "Dữ liệu không hợp lệ" });
            }

            var scamPost = new ScamPost
            {
                NameScam = model.NameScam,
                StkScam = model.StkScam,
                SdtScam = model.SdtScam,
                NoiDung = model.NoiDung,
                Status = "pending"
            };
            _context.ScamPosts.Add(scamPost);
            await _context.SaveChangesAsync();
            return StatusCode(201, new { id = scamPost.Id });
        }

        public IActionResult Instructions()
        {
            return View();
        }
    }
}