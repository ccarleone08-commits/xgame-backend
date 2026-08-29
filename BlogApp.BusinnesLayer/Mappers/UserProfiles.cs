using AutoMapper;
using BlogApp.BusinnesLayer.DTOs.UserDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.Mappers;

public class UserProfiles : Profile
{
    public UserProfiles()
    {
        CreateMap<RegisterCreateDto, User>()
            .ForMember(x => x.PasswordHash, x => x.MapFrom(y => HashHelper.HashPassword(y.Password)))
            .ForMember(i => i.Image, z => z.Ignore());
        CreateMap<User, UserDto>();
    }
}
