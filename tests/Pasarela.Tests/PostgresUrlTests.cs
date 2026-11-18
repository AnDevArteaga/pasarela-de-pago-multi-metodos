using Npgsql;

namespace Pasarela.Tests;

public class PostgresUrlTests
{
    [Fact]
    public void Decodes_the_database_name_in_a_postgres_url()
    {
        var normalized = PostgresUrl.Normalize("postgres://user:pass@localhost/pagos%20demo");
        Assert.Equal("pagos demo", new NpgsqlConnectionStringBuilder(normalized).Database);
    }
}
