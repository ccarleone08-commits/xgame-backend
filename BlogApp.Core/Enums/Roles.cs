namespace BlogApp.Core.Enums;

public enum Roles
{
    Admin = 1,
    Viewer = 2,
    Editor = 4,
    User = 8,
    MiddleAdmin = 16,
    Moderator = 32,
    SupportAdmin = 64,
    SupportWorker = 128,
    DepositAdmin = 256,
    DepositWorker = 512,
    Bank = 1024,
    WithdrawAdmin = 2048,
    WithdrawWorker = 4096,
    WithdrawBank = 8192
}