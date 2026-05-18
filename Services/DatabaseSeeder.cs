using Microsoft.Data.Sqlite;

namespace ChatAI.Services;

public sealed class DatabaseSeeder
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(IConfiguration configuration, ILogger<DatabaseSeeder> logger)
    {
        _connectionString = configuration.GetConnectionString("Sqlite") ?? "Data Source=chatai.db";
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS products (
                id       INTEGER PRIMARY KEY,
                name     TEXT    NOT NULL,
                category TEXT    NOT NULL,
                price    REAL    NOT NULL,
                stock    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS orders (
                id         INTEGER PRIMARY KEY,
                product_id INTEGER NOT NULL REFERENCES products(id),
                customer   TEXT    NOT NULL,
                quantity   INTEGER NOT NULL,
                total      REAL    NOT NULL,
                order_date TEXT    NOT NULL,
                status     TEXT    NOT NULL CHECK(status IN ('pending','shipped','delivered','cancelled'))
            );

            INSERT OR IGNORE INTO products (id, name, category, price, stock) VALUES
                (1, 'Wireless Headphones', 'Electronics',  79.99, 120),
                (2, 'Mechanical Keyboard', 'Electronics',  49.99,  85),
                (3, 'USB-C Hub',           'Accessories',  29.99, 200),
                (4, 'Standing Desk Mat',   'Furniture',    39.99,  60),
                (5, 'Monitor Stand',       'Furniture',    24.99,  95),
                (6, 'Webcam HD',           'Electronics',  59.99,  45),
                (7, 'Blue Light Glasses',  'Accessories',  19.99, 180),
                (8, 'Laptop Sleeve',       'Accessories',  14.99, 300);

            INSERT OR IGNORE INTO orders (id, product_id, customer, quantity, total, order_date, status) VALUES
                (1,  1, 'Alice Johnson',  1,  79.99, '2026-04-01', 'delivered'),
                (2,  3, 'Bob Smith',      2,  59.98, '2026-04-03', 'delivered'),
                (3,  2, 'Carol White',    1,  49.99, '2026-04-10', 'shipped'),
                (4,  5, 'Alice Johnson',  2,  49.98, '2026-04-12', 'delivered'),
                (5,  6, 'Dave Brown',     1,  59.99, '2026-04-15', 'shipped'),
                (6,  1, 'Eve Davis',      1,  79.99, '2026-04-20', 'pending'),
                (7,  4, 'Frank Miller',   1,  39.99, '2026-04-22', 'pending'),
                (8,  8, 'Bob Smith',      3,  44.97, '2026-04-25', 'delivered'),
                (9,  7, 'Carol White',    2,  39.98, '2026-05-01', 'shipped'),
                (10, 2, 'Grace Lee',      1,  49.99, '2026-05-05', 'pending');
            """;

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Database seeded");
    }
}
