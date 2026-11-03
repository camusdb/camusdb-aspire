# CamusDB.Aspire.Hosting

The Aspire hosting integration for [CamusDB](https://github.com/camusdb/camusdb). Use it in an Aspire
AppHost project. It runs a standalone CamusDB node or a CamusDB cluster in containers, creates
databases on it, and gives connection strings to the projects that reference it.

## Install

```shell
dotnet add package CamusDB.Aspire.Hosting
```

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var camus = builder.AddCamusDB("camus")
    .WithDataVolume();

var appDb = camus.AddDatabase("appdb");

builder.AddProject<Projects.ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb);

builder.Build().Run();
```

`AddCamusDB` runs the `docker.io/camusdb/camusdb:0.17.0` image. The container has two endpoints:

| Endpoint | Container port | API |
| --- | --- | --- |
| `http` | 5095 | JSON/REST API and `/health` |
| `grpc` | 5096 | gRPC API, plaintext HTTP/2 |

The AppHost does an HTTP health check on `/health` of the `http` endpoint.

CamusDB does not create a database on first use. When the server becomes healthy, the AppHost runs
`CREATE DATABASE IF NOT EXISTS` for each `AddDatabase` resource. A database resource becomes healthy
only after that, so `WaitFor(appDb)` starts the project only when the database exists.

## Connection strings

The connection string uses the keys of the `CamusDB.Client` ADO.NET provider:

```text
Endpoint=http://localhost:61234;Database=appdb
```

A server resource gives a connection string with no `Database` key. A `CamusConnection` cannot open
without that key, so reference a database resource.

To use the gRPC API, select the protocol:

```csharp
builder.AddCamusDB("camus").WithProtocol(CamusDBProtocol.Grpc);
// Endpoint={grpc url};Protocol=grpc;BackupEndpoint={http url};Database=appdb
```

A referenced resource also gets these connection properties as environment variables, for example
`APPDB_HOST`: `Host`, `Port`, `GrpcPort`, `Uri`, `GrpcUri`, `Database`, and with authentication on,
`Username` and `Password`.

## Authentication

Authentication is off by default, as in CamusDB. To turn it on:

```csharp
var password = builder.AddParameter("camus-password", secret: true);

builder.AddCamusDB("camus")
    .WithAuthentication(password: password);   // the user is "admin"
```

When you do not give a password, Aspire generates one. The integration also generates the token key
(`CAMUSDB_AUTH_TOKEN_KEY`). The connection strings then carry `User`, `Password` and
`AllowInsecureCredentials=true`.

Read these limits before you use it:

- The server reads the bootstrap user and password only when its user catalog is empty. With a data
  volume, a changed password has no effect on a server that already has users.
- The password must not contain a single quote (`'`).
- The container serves plaintext HTTP, and other containers reach it by a host name that is not
  loopback. Thus the integration starts the server with `--require-tls-when-auth-enabled=false`.
  This setup is for local development only.

## Other settings

The server reads its log levels and its partition count from environment variables:

```csharp
builder.AddCamusDB("camus")
    .WithEnvironment("CAMUS_LOG_LEVEL", "Debug")
    .WithEnvironment("CAMUS_INITIAL_PARTITIONS", "8");
```

Use `WithImageTag("0.16.4")` to run a different server version. Use `WithDataBindMount(path)` to keep
the data in a host folder instead of a volume.

## Cluster

`AddCamusDBCluster` runs several CamusDB nodes in
[cluster mode](https://camusdb.github.io/docs/cluster/). The nodes replicate each write through Raft,
so the database continues to work when one node stops.

```csharp
var camus = builder.AddCamusDBCluster("camus", nodes: 3)
    .WithDataVolume();

var appDb = camus.AddDatabase("appdb");

builder.AddProject<Projects.ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb);
```

The cluster resource `camus` has no container of its own. It has one container for each node:
`camus-1`, `camus-2` and `camus-3`. Each node has the same `http` and `grpc` endpoints as a standalone
server. The nodes reach each other by these names on the container network, on the Raft port 7070. The
Raft port is not published to the host.

Use an odd number of nodes. A cluster commits while a majority of its nodes runs. Thus 3 nodes continue
after the loss of 1 node, and 5 nodes continue after the loss of 2 nodes. A one-node cluster
(`nodes: 1`) is also possible. It cannot continue after the loss of its node, but it can grow later.

### Connection strings

The connection string lists the endpoint of each node:

```text
Endpoint=http://localhost:61234,http://localhost:61236,http://localhost:61238;Database=appdb
```

The `CamusDB.Client` provider sends the requests to the endpoints in turn. When a node does not
answer, the provider skips that node for 30 seconds. The request that finds the node down fails and is
not sent again, so an application that must hide a node failure needs its own retry. `WithProtocol(CamusDBProtocol.Grpc)` lists the `grpc` endpoints, and it
sets `BackupEndpoint` to the `http` endpoint of the first node.

The connection properties of a cluster are `Uri` and `GrpcUri`, each a comma-separated list, and
`Database`. With authentication on, they also include `Username` and `Password`. A cluster has no `Host`,
`Port` or `GrpcPort` property, because it has more than one host.

### Health and startup

Each node checks `/v1/cluster/health`. This endpoint answers 200 only when the node can serve requests.
The `/health` endpoint answers before the cluster elects its leaders, so the integration does not use
it for a cluster.

At the first start, the cluster resource becomes healthy when all its nodes are healthy. The AppHost
then creates the databases, and each database resource becomes healthy after that.

After the first start, the cluster stays healthy while a majority of its nodes is healthy, because a
majority can still commit. The health description gives the count, for example "2 of 3 nodes are
healthy". The cluster does not report a degraded state, because Aspire waits for a healthy state only:
with a degraded cluster, no resource that waits for the cluster could restart while one node is down.
Without a majority, the cluster is unhealthy. When all nodes stop, all nodes must be healthy again
before the cluster is healthy.

### TLS between the nodes

The nodes connect to each other over TLS, and each node checks that the certificate of a peer names
its host. On each run, the AppHost makes a certificate authority (CA) for the cluster and one
certificate for each node. It copies them into `/camusdb/certs` in each container. Each node trusts the
CA through the `SSL_CERT_FILE` environment variable. The AppHost keeps the node certificates in a
temporary folder, and it deletes the folder when it stops.

These certificates are for local development only. The client endpoints of the nodes stay plaintext
HTTP and h2c, as on a standalone server. A node in cluster mode serves TLS on its `grpc` endpoint by
default, so each node gets a `/camusdb/config.yml` with `grpc_tls_enabled: false`.

### Authentication

`WithAuthentication` on a cluster works as on a server, with two more settings:

- The AppHost generates a node secret (`CAMUSDB_NODE_SECRET`) and one token key for all nodes. A token
  from one node is thus valid on each node.
- The nodes authenticate each other with mutual TLS. The `config.yml` of each node also gets
  `raft_mutual_tls_enabled: true` and `raft_peer_ca` set to the cluster CA. The Raft port then accepts
  only a peer that shows a certificate from this CA.

The limits of authentication on a server also apply to a cluster.

### Data

`WithDataVolume()` adds one volume for each node. `WithDataVolume("camus-data")` names them
`camus-data-1`, `camus-data-2`, and so on. `WithDataBindMount(path)` mounts the subfolder
`path/camus-1` on the node `camus-1`, and so on.

### Node settings

All nodes must run the same image and the same settings. Use `ConfigureNodes` to apply a setting to
each node:

```csharp
builder.AddCamusDBCluster("camus")
    .ConfigureNodes(node => node
        .WithImageTag("0.16.4")
        .WithEnvironment("CAMUS_LOG_LEVEL_KOMMANDER", "Information")
        .WithEnvironment("CAMUS_INITIAL_PARTITIONS", "8"));
```

The nodes start with `--memory-profile=dev`, which keeps the caches of each node to about 96 MiB. Set
`CAMUS_MEMORY_PROFILE` to `prod` to size the caches from the memory of the machine.
