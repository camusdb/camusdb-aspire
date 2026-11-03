# CamusDB cluster example

A small Aspire application that runs a CamusDB cluster of three nodes, and a web page that uses it.

| Resource | What it is |
| --- | --- |
| `camus` | The cluster. It has no container of its own. |
| `camus-1`, `camus-2`, `camus-3` | The three nodes, each in its own container and with its own data volume. |
| `notesdb` | A database on the cluster. The AppHost creates it when all nodes are healthy. |
| `api` | A minimal ASP.NET Core application with one page. |

## Run it

The example needs Docker and the .NET 10 SDK. From the root of the repository:

```shell
aspire run --apphost examples/ClusterExample/ClusterExample.AppHost/ClusterExample.AppHost.csproj
```

You can also use `dotnet run --project examples/ClusterExample/ClusterExample.AppHost`.

The Aspire dashboard opens. When all resources are healthy, open the **Notes** URL of the `api`
resource.

## What the page shows

- **Nodes:** the page asks each node, by itself, for the number of notes. All nodes show the same
  number, because the cluster replicates each write to every node.
- **Notes:** the form adds a note through the cluster connection string. The client sends each request
  to the next node in turn.

## Stop a node

1. In the dashboard, stop `camus-3`.
2. Reload the page. `camus-3` shows as down, and the other two nodes still answer.
3. Add a note. The write succeeds, because two of three nodes are a majority.
4. Start `camus-3` again, and reload the page. `camus-3` catches up and shows all the notes.

While `camus-3` is down, the `camus` resource stays healthy, and its health description says
"2 of 3 nodes are healthy". Stop a second node, and the cluster becomes unhealthy, because one node of
three is not a majority.

When a request goes to a node that is down, that request fails, and the client skips the node for 30
seconds. The client does not send the request again. Thus the API tries each write one more time, and the
second try goes to another node. See `RetryOnceAsync` in `ClusterExample.Api/Program.cs`.

## Clean data

The nodes keep their data in volumes, so the notes stay when you run the example again. To start with
no data, stop the AppHost and remove the three volumes:

```shell
docker volume ls --filter name=camus --format '{{.Name}}'
docker volume rm <the three volume names>
```

After `aspire stop`, wait until the old node containers are gone before you run the example again. A
new node cannot open its data while an old container still holds the same volume.
