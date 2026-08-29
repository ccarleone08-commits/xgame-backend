using BlogApp.BusinnesLayer.DTOs.CategoryDTOs;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.BusinnesLayer.Services.Abstracts;

public class CategoryService(ICategoryRepository _repo) : ICategoryService
{
    public async Task<int> CreateAsync(CategoryCreateDto dto)
    {
        Category category = dto;
        await _repo.AddAsync(category);
        await _repo.SaveAsync();
        return category.Id;
    }

    public async Task<List<CategoryListItem>> GetAllAsync()
    {
        return await _repo.GetAll().Select(x => new CategoryListItem
        {
            Id = x.Id,
            Name = x.Name,
            Icon = x.Icon,
        }).ToListAsync();
    }
}
