using AutoMapper;
using ConsumeWebAPI.Models;
using Microsoft.AspNetCore.Mvc;

namespace ConsumeWebAPI.Controllers;
[Route("api/[controller]/[action]")]
public class CategoryController : Controller
{
    Uri MyAPIAddress = new Uri("https://localhost:7046/api");
    private readonly HttpClient _client;
    private readonly IMapper _mapper;
    public CategoryController()
    {
        _client = new HttpClient();
        _client.BaseAddress = MyAPIAddress;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        List<CategoryVM> categorylist = new List<CategoryVM>();
        HttpResponseMessage response = await _client.GetAsync(_client.BaseAddress + "/Categories/Get");
        if (response.IsSuccessStatusCode)
        {
            string data = await response.Content.ReadAsStringAsync();
            categorylist = _mapper.Map<List<CategoryVM>>(data);
        }
        return View(categorylist);
    }
}
