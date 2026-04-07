using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NCBA.DCL.Data;

#nullable disable

namespace NCBA.DCL.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260329090000_AddUserPasswordLockout")]
    public partial class AddUserPasswordLockout
    {
    }
}