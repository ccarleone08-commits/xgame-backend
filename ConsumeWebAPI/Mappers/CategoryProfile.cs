using AutoMapper;
using BlogApp.BusinnesLayer.DTOs.CategoryDTOs;
using ConsumeWebAPI.Models;

namespace BlogApp.BusinnesLayer.Mappers;

public class CategoryProfiles : Profile
{
    public CategoryProfiles()
    {
        CreateMap<CategoryCreateDto, CategoryVM>();
    }
}
