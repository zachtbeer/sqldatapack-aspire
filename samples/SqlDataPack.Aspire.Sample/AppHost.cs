var builder = DistributedApplication.CreateBuilder(args);

// No data volume on purpose. A volume keeps the sa password from the run that created it,
// while Aspire generates a fresh one each run, so the two stop matching and the resource goes
// unhealthy. A blank database every run is also what V1 import expects anyway.
var sql = builder.AddSqlServer("sql");

sql.AddDatabase("catalog").WithSqlDataPack();

builder.Build().Run();
