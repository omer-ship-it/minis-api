using Microsoft.EntityFrameworkCore;

namespace Minis.Api.Services
{
    public class MinisDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }
        public DbSet<MiniApp> MiniApps { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlServer("Server=localhost\\SQLEXPRESS02;Database=MINIS;Trusted_Connection=True;TrustServerCertificate=True;");
        }
    }

    public class Product
    {
        public int Id { get; set; }
        public string? SquareId { get; set; }
        public int MiniAppId { get; set; }
        public string Name { get; set; }
        public decimal? Price { get; set; }
        public string Category { get; set; }
        public string Image { get; set; }
        public string JsonData { get; set; }
        public int Sort { get; set; }
        public bool Status { get; set; }
    }

    public class MiniApp
    {
        public int Id { get; set; }
        public string Customization { get; set; }
    }
}
