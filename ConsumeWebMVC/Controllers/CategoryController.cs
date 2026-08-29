using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using ConsumeWebMVC.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConsumeWebMVC.Controllers
{
    public class CategoryController(BlogAppDbContext _context) : Controller
    {
        public async Task<IActionResult> Index()
        {
            var categoryVMs = await _context.Categories.Select(c => new CategoryVM
            {
                Id = c.Id,
                Name = c.Name,
                Icon = c.Icon,
            }).ToListAsync();

            return View(categoryVMs);
        }
        [HttpPost]
        public async Task<IActionResult> Create(CategoryCreateVM vm)
        {
            Category cat = new Category
            {
                Name = vm.Name,
                Icon = vm.Icon,
            };
            await _context.AddAsync(cat);
            await _context.SaveChangesAsync();
            return View();
        }
    }
}

