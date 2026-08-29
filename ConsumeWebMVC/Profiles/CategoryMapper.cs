using AutoMapper;
using BlogApp.BusinnesLayer.DTOs.CategoryDTOs;
using ConsumeWebMVC.Models;

namespace ConsumeWebMVC.Profiles
{
    public class CategoryMapper : Profile
    {
        public CategoryMapper()
        {
            CreateMap<CategoryCreateDto, CategoryVM>();
        }
    }
}
