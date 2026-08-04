using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConcertBooking.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketCategoryPriceCheckConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_TicketCategory_Price",
                table: "TicketCategories",
                sql: "\"Price\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TicketCategory_Price",
                table: "TicketCategories");
        }
    }
}
