using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using Microsoft.Data.SqlClient;

namespace Tutorial9.Controllers
{
    public class WarehouseController : Controller
    {
        private readonly IConfiguration _configuration;

        public WarehouseController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public class WarehouseRequest
        {
            public int IdProduct { get; set; }
            public int IdWarehouse { get; set; }
            public int Amount { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> AddProductToWarehouse([FromBody] WarehouseRequest request)
        {
            if (request.Amount <= 0)
                return BadRequest("Amount must be greater than 0.");

            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // 1. Sprawdzenie, czy produkt istnieje
                        var checkProductCmd = new SqlCommand("SELECT COUNT(1) FROM Product WHERE IdProduct = @IdProduct", connection, transaction);
                        checkProductCmd.Parameters.AddWithValue("@IdProduct", request.IdProduct);
                        var productExists = (int)await checkProductCmd.ExecuteScalarAsync() > 0;
                        if (!productExists)
                            return NotFound("Product not found.");

                        // 1. Sprawdzenie, czy magazyn istnieje
                        var checkWarehouseCmd = new SqlCommand("SELECT COUNT(1) FROM Warehouse WHERE IdWarehouse = @IdWarehouse", connection, transaction);
                        checkWarehouseCmd.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
                        var warehouseExists = (int)await checkWarehouseCmd.ExecuteScalarAsync() > 0;
                        if (!warehouseExists)
                            return NotFound("Warehouse not found.");

                        // 2. Sprawdzenie zamówienia
                        var orderCmd = new SqlCommand(@"
                            SELECT TOP 1 o.IdOrder, o.Amount, p.Price
                            FROM [Order] o
                            JOIN Product p ON o.IdProduct = p.IdProduct
                            LEFT JOIN Product_Warehouse pw ON o.IdOrder = pw.IdOrder
                            WHERE o.IdProduct = @IdProduct AND o.Amount = @Amount 
                                AND o.CreatedAt < @CreatedAt
                                AND pw.IdProductWarehouse IS NULL", connection, transaction);
                        orderCmd.Parameters.AddWithValue("@IdProduct", request.IdProduct);
                        orderCmd.Parameters.AddWithValue("@Amount", request.Amount);
                        orderCmd.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);

                        int? orderId = null;
                        decimal? productPrice = null;

                        using (var reader = await orderCmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                orderId = reader.GetInt32(0);
                                productPrice = reader.GetDecimal(2);
                            }
                        }

                        if (orderId == null)
                            return BadRequest("No matching order found or it was already fulfilled.");

                        // 4. Update FulfilledAt
                        var updateOrderCmd = new SqlCommand("UPDATE [Order] SET FulfilledAt = @FulfilledAt WHERE IdOrder = @IdOrder", connection, transaction);
                        updateOrderCmd.Parameters.AddWithValue("@FulfilledAt", DateTime.UtcNow);
                        updateOrderCmd.Parameters.AddWithValue("@IdOrder", orderId.Value);
                        await updateOrderCmd.ExecuteNonQueryAsync();

                        // 5. Insert do Product_Warehouse
                        var insertCmd = new SqlCommand(@"
                            INSERT INTO Product_Warehouse(IdWarehouse, IdProduct, IdOrder, Amount, Price, CreatedAt)
                            VALUES (@IdWarehouse, @IdProduct, @IdOrder, @Amount, @Price, @CreatedAt);
                            SELECT SCOPE_IDENTITY();", connection, transaction);
                        insertCmd.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
                        insertCmd.Parameters.AddWithValue("@IdProduct", request.IdProduct);
                        insertCmd.Parameters.AddWithValue("@IdOrder", orderId.Value);
                        insertCmd.Parameters.AddWithValue("@Amount", request.Amount);
                        insertCmd.Parameters.AddWithValue("@Price", productPrice.Value * request.Amount);
                        insertCmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

                        var newId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());

                        await transaction.CommitAsync();

                        return Ok(new { IdProductWarehouse = newId });
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        return StatusCode(500, $"Internal server error: {ex.Message}");
                    }
                }
            }
        }
        
        public IActionResult Index()
        {
            return View();
        }
    }
}
