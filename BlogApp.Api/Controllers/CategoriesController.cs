using BlogApp.BusinnesLayer.DTOs.CategoryDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BlogApp.Api.Controllers;

[Route("api/[controller]/[action]")]
[ApiController]
public class CategoriesController(ICategoryService _service) : ControllerBase
{
    [HttpGet("[action]")]
    public async Task<IActionResult> String(string password)
    {
        return Ok(HashHelper.HashPassword(password));
    }
    [HttpGet("[action]")]
    public async Task<IActionResult> Verify(string hashedPassword, string password)
    {
        return Ok(HashHelper.VerifyHashedPassword(hashedPassword, password));
    }


    [HttpGet("[action]")]
    public async Task<IActionResult> Get()
    {
        return Ok(await _service.GetAllAsync());
    }
    [HttpPost]
    public async Task<IActionResult> Post(CategoryCreateDto dto)
    {
        return Ok(await _service.CreateAsync(dto));
    }
}