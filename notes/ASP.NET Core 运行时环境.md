# ASP.NET Core 运行时环境

在ASP.NET Core中，运行时环境和任何请求通过的管道与以前版本的ASP.NET中的内容完全不同。此外，新的ASP.NET Core运行时环境由系统提供的嵌入式依赖注入（DI）基础架构提供支持，该基础架构像幽灵朋友一样静静地监视处理传入请求的所有步骤。

本文将进一步深入探讨ASP.NET Core运行时环境及其组件的内部体系结构，主要是Kestrel服务器和请求中间件。



## ASP.NET Core Host

ASP.NET Core应用程序的核心是一个独立的控制台应用程序，它为实际的应用程序模型（很可能是MVC应用程序模型）设置主机环境。主机负责配置侦听传入HTTP请求，并将请求传递到处理管道的服务器。以下代码展示了典型ASP.NET Core应用程序的主机程序的默认实现，因为它来自标准的Visual Studio 2017模板。以下源代码编写在ASP.NET Core项目的program.cs文件中。

```c#
public class Program
{
    public static void Main(string[] args)
    {
        BuildWebHost(args).Run();
    }

    public static IWebHost BuildWebHost(string[] args) =>
        WebHost.CreateDefaultBuilder(args)
            .UseStartup<Startup>()
            .Build();
}
```

让我们了解更多关于Web主机组件以及启动主机所需的其他更简单的选项。

### WebHost类

WebHost是一个静态类，它提供了两种方法来创建使用预定义设置公开IWebHostBuilder接口的类实例。该类还提供了许多方法来快速启动环境，只传递要侦听的URL和要实现的行为的委托。

#### 配置主机的行为

WebHost类的Start方法允许你以多种方式设置Web应用程序。

```c#
using (var host = WebHost.Start(
     app => app.Response.WriteAsync("Programming ASP.NET Core")))
{
    // Wait for the host to end
    ...
}
```

应用程序所做的全部工作是运行指定的函数，而不管调用的URL是什么。WebHost类的Start方法返回的实例是IWebHost类型，表示应用程序已启动的主机环境。在WebHost.Start方法中，运行以下伪代码：

```c#
public static IWebHost Start(RequestDelegate app) 
{
   var defaultBuilder = WebHost.CreateDefaultBuilder();
   var host = defaultBuilder.Build();

   // 这一行实际上启动了主机
   host.Start();
   return host;
}
```

注意，Start方法以非阻塞方式运行主机，这意味着主机需要一些额外的指令来继续侦听传入的请求。这是一个例子：

```c#
public static void Main(string[] args)
{
     using (var host = WebHost.Start(
         app => app.Response.WriteAsync("Programming ASP.NET Core")))
         {
            // Wait for the host to end
            Console.WriteLine("Courtesy of 'Programming ASP.NET Core'\n====");
            Console.WriteLine("Use Ctrl-C to shut down the host...");
            host.WaitForShutdown();
         }
}
```

运行代码效果如下：

![webhost_run_result](assets/webhost_run_result.jpg)

默认情况下，主机侦听端口5000上的传入请求。从图中可以看出，即使在用户级代码中没有明显的行被打开，日志记录器也会自动打开。这意味着主机接收一些默认配置。 WebHost.CreateDefaultBuilder方法由Start方法在内部调用，负责接收默认配置。我们来了解有关默认设置的更多信息。

#### 默认设置

在ASP.NET Core 2.0中，方法CreateDefaultBuilder（在WebHost类上定义为静态方法）创建并返回主机对象的实例。 WebHost上定义的所有Start方法最终都在内部调用默认构建器。以下是调用默认Web主机构建器时发生的情况。

```c#
public static IWebHostBuilder CreateDefaultBuilder(string[] args)
{
   return new WebHostBuilder()
           .UseKestrel()
           .UseContentRoot(Directory.GetCurrentDirectory())
           .ConfigureAppConfiguration(
            (Action<WebHostBuilderContext, IConfigurationBuilder>) ((context, config) =>
                {
                   var env = context.HostingEnvironment;
                   config.AddJsonFile("appsettings.json", true, true)
                         .AddJsonFile(string.Format("appsettings.{0}.json", 
                                     env.EnvironmentName), true, true);
                   if (env.IsDevelopment())
                   {
                      var assembly = Assembly.Load(new AssemblyName(env.ApplicationName));
                      if (assembly != null)
                         config.AddUserSecrets(assembly, true);
                   }
                   config.AddEnvironmentVariables();
                   config.AddCommandLine(args);
                  }))
            .ConfigureLogging(
              (Action<WebHostBuilderContext, ILoggingBuilder>) ((context, logging) => 
                 {                  logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                      logging.AddConsole();
					  logging.AddDebug();
                 }))
            .UseIISIntegration()
            .UseDefaultServiceProvider(
                (Action<WebHostBuilderContext, ServiceProviderOptions>) ((context, options) =>  
                    {
                      options.ValidateScopes = context.HostingEnvironment.IsDevelopment()));
                    }
    }
```

总之，默认构建器做了6件不同的事情，如下表所示：

| Action           | 描述                                                         |
| ---------------- | ------------------------------------------------------------ |
| Web server       | 添加Kestrel作为ASP.NET Core管道的嵌入式Web服务器             |
| Content root     | 将当前目录设置为Web应用程序访问的任何基于文件的内容的根文件夹。 |
| Configuration    | 添加一些配置提供程序：appsettings.json，环境变量，命令行参数和用户机密（仅在开发模式下）。 |
| Logging          | 添加一些日志记录提供程序：在配置树的日志记录部分中定义的日志提供程序以及控制台和调试记录程序。 |
| IIS              | 启用与IIS集成作为反向代理。                                  |
| Service provider | 配置默认服务提供者。                                         |

需要注意的是，每当您调用WebHost类中的一个方法来启动web应用程序的主机时，所有这些操作总是发生在您的控制之外。如果你想为主机建立一个自定义的设置集合，请继续阅读。然而，在研究自定义主机配置之前，让我们先研究一下实际运行主机并让它侦听传入调用的选项。

#### 启动主机

每当通过WebHost类公开的任何方法创建主机时，都会收到一个已经启动的主机，该主机已经在侦听配置好的地址。如之前所述，默认情况下使用的Start方法以非阻塞方式启动主机，但也存在其他选项。

Run方法启动Web应用程序，然后阻塞调用线程，直到主机关闭。相反，WaitForShutdown方法会阻塞调用线程，直到手动触发应用程序关闭，例如通过Ctrl + C.

### 自定义主机设置

使用默认主机构建器非常简单，并且可以为主机提供您仍希望拥有的大多数功能。您可以使用其他方面进一步扩展主机，例如启动类和要侦听的URL。或者，您可以拥有一个功能比默认主机更少的主机。

#### 手动创建Web主机

以下代码显示了如何从头开始创建y一个全新主机。

```c#
var host = new WebHostBuilder().Build();
```

WebHostBuilder类有许多扩展方法来添加功能。至少，您需要指定要使用的进程内HTTP服务器实现。这个Web服务器侦听HTTP请求，并将其转发给包装在HttpContext包中的应用程序。 Kestrel是默认的，也是最常用的Web服务器实现。要启用Kestrel，需要调用UseKestrel方法。

要使Web应用程序与IIS主机兼容，还需要通过UseIISIntegration扩展方法启用该功能。最后，您可能希望指定内容根文件夹和用于完成运行时环境配置的启动类。

```c#
var host = new WebHostBuilder()
               .UseKestrel() 
               .UseIISIntegration()
               .UseContentRoot(Directory.GetCurrentDirectory())
               .Build();
```

此时必须指定应用程序的另外两个方面。一个是加载应用程序设置，另一个是终止中间件。在ASP.NET Core 2.0中，您可以使用新的ConfigureAppConfiguration方法加载应用程序设置，如上面显示的代码段中所示。相反，可以使用Configure方法添加终止中间件，即处理任何传入请求的代码。

```c#
var host = new WebHostBuilder()
               .UseKestrel() 
               .UseIISIntegration()
               .UseContentRoot(Directory.GetCurrentDirectory())
               .Configure(app => {
                    app.Run(async (context) => {
                        var path = context.Request.Path;
                        await context.Response.WriteAsync("<h1>" + path + "</h1>");
                    });
                })
                .Build();
```

应用程序设置，终止中间件和各种可选的中间件组件也可以在Startup类中更加轻松地指定。Startup类只是通过UseStartup方法传递给Web主机构建器实例的另一个相关参数。

```c#
var host = new WebHostBuilder()
               .UseKestrel() 
               .UseIISIntegration()
               .UseContentRoot(Directory.GetCurrentDirectory())
               .UseStartup<Startup>()
               .Build();
```

从功能上讲，上面的代码片段提供了许多足以运行ASP.NET Core 2.0应用程序的功能。

#### 定位启动类

可以通过多种方式指定启动类。最常见的是使用UseStartup <T>扩展方法的通用版本，其中类型T标识启动类。前面的代码片段对此进行了演示。

您还可以使用非泛型形式的UseStartup方法，并将.NET类型引用作为参数传递：

```c#
var host = new WebHostBuilder()
               .UseKestrel() 
               .UseIISIntegration()
               .UseContentRoot(Directory.GetCurrentDirectory())
               .UseStartup(typeof(MyStartup))
               .Build();
```

最后，还可以通过程序集名称指定启动类型：

```c#
var host = new WebHostBuilder()
               .UseKestrel() 
               .UseIISIntegration()
               .UseContentRoot(Directory.GetCurrentDirectory())
               .UseStartup(Assembly.Load(new AssemblyName("Ch14.Builder")).FullName)
               .Build();
```

如果选择将程序集名称传递给UseStartup，则假定程序集包含一个名为Startup或StartupXxx的类，其中Xxx与当前托管环境（开发，生产或其他任何内容）相匹配。

#### 应用程序生命周期

在ASP.NET Core 2.0中，支持三个应用程序生命周期事件，供开发人员来执行启动和关闭任务。 IApplicationLifetime接口定义了您可以在代码中连接的主机事件。

```c#
public interface IApplicationLifetime
{
   CancellationToken ApplicationStarted { get; }
   CancellationToken ApplicationStopping { get; }
   CancellationToken ApplicationStopped { get; }
   void StopApplication();
}
```

如您所见，除了已启动，停止中和已停止事件外，该接口还提供了一个主动的StopApplication方法。可以在Startup类的Configure方法中添加事件处理代码。

```c#
public void Configure(IApplicationBuilder app, IApplicationLifetime life)
{
    // 配置适当的应用程序的关闭
    life.ApplicationStarted.Register(OnStarted);
    life.ApplicationStopping.Register(OnStopping);
    life.ApplicationStopped.Register(OnStopped);
}
```

当主机启动并运行并等待以编程方式控制的终止时，ApplicationStarted事件会到达您的代码。 ApplicationStopping事件表示应用程序的程序性关闭已经启动，但某些请求可能仍在队列中。主人基本上即将关闭。最后，当队列中没有更多待处理请求时，将触发ApplicationStopped事件。一旦事件的处理终止，主机的实际关闭就会发生。

StopApplication方法是启动Web应用程序主机的编程关闭的接口方法。如果您从dotnet.exe启动器控制台窗口按Ctrl + C，也会以静默方式调用该方法。如果使用以下代码，则预期输出如下图所示：

```c#
private static void OnStarted()
{
    // Perform post-startup activities here
    Console.WriteLine("Started\n=====");
    Console.BackgroundColor = ConsoleColor.Blue;
}

private static void OnStopping()
{
    // Perform on-stopping activities here
    Console.BackgroundColor = ConsoleColor.Black;
    Console.WriteLine("=====\nStopping\n=====\n");
}

private static void OnStopped()
{
    // Perform post-stopped activities here
    var defaultForeColor = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Stopped.");
    Console.ForegroundColor = defaultForeColor;
    Console.WriteLine("Press any key.");
    Console.ReadLine();
}
```

![Application_lifetime_events](assets/Application_lifetime_events.jpg)

如您所见，生命周期事件围绕着Web应用程序的任何活动。

#### 其他设置

Web主机可以通过一系列附加设置进一步自定义，这些设置可以微调行为的一些次要方面。 Web主机的其他设置：

##### CaptureStartupErrors

用于控制启动错误捕获的布尔值。除非整体配置将Kestrel设置为在IIS后面运行，否则默认值为false。如果未捕获错误，则任何异常都将导致主机退出。如果捕获了错误，那么将吞下启动异常，但主机仍会尝试启动已配置的Web服务器。

##### UseEnvironment

以编程方式设置应用程序运行环境。该方法接受一个字符串，该字符串匹配预定义的环境，如开发、生产、登台或任何其他对应用程序有意义的环境名称。通常，环境名称是从环境变量（ASPNETCORE_ENVIRONMENT）中读取的，并且在使用Visual Studio时，可以通过用户界面或launchSettings.json文件设置环境变量。

##### UseSetting

用于直接通过关联键设置选项的通用方法。使用此方法设置值时，无论类型如何，都将值设置为字符串（以引号括起）。该方法可用于配置至少以下设置：

- DetailedErrorsKey：布尔值，指示是否应捕获和报告详细错误。默认值为false。
- HostingStartupAssembliesKey：分号分隔要在启动时加载的附加程序集名称的字符串。默认为空字符串。
- PreventHostingStartupKey：防止自动加载启动程序集，包括应用程序的程序集。默认值为false。
- ShutdownTimeoutKey：指定Web主机在关闭之前等待的秒数。默认值为5秒。请注意，可以使用UseShutdownTimeout扩展方法设置相同的设置。等待使web主机充分处理请求的时间。

属性名称表示为WebHostDefaults枚举的属性。

```c#
WebHost.CreateDefaultBuilder(args)
.UseSetting(WebHostDefaults.DetailedErrorsKey, “true”);
```

##### UseShutdownTimeout

指定等待Web主机关闭的时间。默认值为5秒。该方法接受一个TimeSpan值。



您可能想知道为什么可以对Web主机配置进行如此极其详细的控制。您可能也想知道为什么应用程序设置的配置可以在Web主机级别的program.cs中完成，远在应用程序实际启动之前。答案是双重的。首先，是完整性。其次，是促进集成测试的能力。在Web主机的设置中具有很大的灵活性，可以轻松创建所有内容都相同的重复项目，但是主机和主机配置可以安排匹配给定的集成场景。

当涉及到表示web主机的配置时，设置的顺序很重要，但总的来说，它遵循一个基本规则：生效的是最后一个设置。因此，如果您指出多个启动类，那么不会抛出任何错误，但只是指定的最后一个设置优先。



## 嵌入式HTTP服务器

一个ASP.NET Core应用程序需要一个进程内HTTP服务器来运行。Web主机启动HTTP服务器并使其侦听任何已配置的端口和URL。 HTTP服务器将捕获传入的请求并将它们推送到ASP.NET Core管道，配置的中间件将在其中处理它。下图展示了整个体系结构：

 ![runtimearchitecture](assets/runtimearchitecture.jpg)

图中的图表显示了ASP.NET Core内部HTTP服务器和Internet空间之间的直接连接。实际上，这种直接连接是可选的。实际上，您可以选择在中间放置一个反向代理，以防止内部HTTP服务器被开放的Internet访问。 （稍后会做介绍）

### 选择HTTP服务器

上图中的内部HTTP服务器有两种类型。它可以基于Kestrel，也可以基于名为http.sys的内核级驱动程序。在这两种情况下，实现都会侦听一组配置好的端口和URL，并将任何传入的请求发送给ASP.NET Core 2.0管道。

#### Kestrel vs. Http.sys

ASP.NET Core 2.0内部HTTP服务器最常见的选择是Kestrel，它是一个基于libuv的跨平台Web服务器。特别是，libuv是一个跨平台的异步I/O库。从Visual Studio中创建新的ASP.NET Core项目时，模板使用Kestrel作为Web服务器提供代码。 .NET Core支持的所有平台和版本都支持Kestrel。

Kestrel的替代方案是使用http.sys，它是一个只支持Windows的HTTP服务器，它依赖于旧的忠实Windows http.sys内核驱动程序的服务。通常，除了一些特定情况之外，您可能总是希望使用Kestrel。在版本2.0发布之前，不建议将Kestrel用于那些不需要使用反向代理来保护应用程序免受公共Internet访问的场景。在这方面，http.sys代表了一种更可靠的选择（尽管仅限于Windows平台），因为它基于更成熟的技术。此外，http.sys是特定于Windows的，并且支持Kestrel设计之外的功能，例如Windows身份验证。

以下代码显示如何在ASP.NET Core应用程序中启用http.sys。

```c#
var host = new WebHostBuilder().UseHttpSys().Build();
```

推荐使用Kestrel，因为Kestrel是跨平台的，并且将作为一个足够强大的Web服务器得到越来越多的改进，它能够在没有反向代理障碍的情况下支持开放的Internet，它将不断得到改进。

当Kestrel不适合时，您可以使用Windows外部的反向代理(例如Nginx或Apache)。相反，在Windows下，可以直接选择http.sys或IIS。

注意有关使用http.sys所需的额外配置的更多信息，请参阅：https://docs.microsoft.com/en-us/aspnet/core/fundamentals/servers/httpsys

#### 指定的URL

可以将内部HTTP服务器配置为侦听各种URL和端口。您可以通过Web主机构建器类型上定义的UseUrls扩展方法指定此信息。

```c#
var host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("...")
                ...
                .Build();
```

UseUrls方法指示服务器应侦听传入请求的端口和协议的主机地址。如果要指定多个URL，可以使用分号分隔它们。默认情况下，内部Web服务器被指示侦听端口5000上的本地主机。使用*通配符指示服务器应该使用指定的端口和协议侦听任何主机名上的请求。例如，以下代码也是可以接受的。

```c#
var host = new WebHostBuilder()
.UseKestrel()
.UseUrls("http://*:7000")
...
.Build();
```

注意，ASP.NET Core内部HTTP服务器的特征是IServer接口。这意味着除了Kestrel和http.sys之外，您甚至可以通过实现接口来创建自己的自定义HTTP服务器。 IServer接口提供成员来配置服务器应侦听请求的端点。默认情况下，要侦听的URL列表是从web主机获取的。但是，可以强制服务器通过它自己的API接受URL列表。您可以使用PreferHostingUrls Web主机扩展方法来实现。

```c#
var host = new WebHostBuilder()
                .UseKestrel()
                .PreferHostingUrls(false)
                ...
                .Build();    
```

#### hosting.json文件

使用UseUrls方法 （甚至使用服务器的特定API来实现端点），带来了一个相关的缺点：URL的名称在应用程序的源代码中是硬编码的，需要更改一个新的编译步骤。为避免这种情况，可以从外部文件hosting.json文件加载HTTP服务器配置。

必须在应用程序文件夹的根目录中创建该文件。这是一个示例，说明如何设置服务器的URL。

```json
{
  "server.urls": "http://localhost:7000;http://localhost:7001"
}
```

要强制加载hosting.json文件，通过调用AddJsonFile将其添加到应用程序设置中。

### 配置反向代理

最初，Kestrel服务器的设计初衷并不是为了暴露于开放的Internet，这意味着出于安全考虑，以及为了保护应用程序免受可能的web攻击，需要在其上使用反向代理。但是，从ASP.NET Core 2.0开始，添加了更厚的防御屏障，从而导致需要考虑更多配置选项。

注意，除了安全原因之外，需要反向代理的一个场景是，当您有多个应用程序共享同一服务器上运行的相同IP和端口时。Kestrel并不仅仅支持这种情况；一旦配置为侦听端口，无论主机头如何，Kestrel都会处理所有通过的流量。

#### 使用反向代理的原因

无论Kestrel背后的应用程序被设计为公开到公共Internet，还是只公开到内部网络，你都可以将HTTP服务器配置为使用或不使用反向代理。一般而言，反向代理是代表客户机从一个或多个服务器检索资源的代理服务器。如下图：

 ![reverse_proxy_scheme](assets/reverse_proxy_scheme.jpg)

反向代理完全屏蔽了来自不同用户代理的实际Web服务器（在我们的例子中是Kestrel服务器）。反向代理通常是一个成熟的Web服务器，它捕获传入的请求，并在进行一些初步工作后将其提供给后端服务器。用户代理完全不知道代理背后的实际服务器，就他们而言，他们实际上是连接到实际的服务器。

如前所述，拥有反向代理的主要原因是安全性和防止潜在有害请求甚至到达实际Web服务器的能力。拥有反向代理的另一个原因是，附加的服务器层有助于设置最合适的负载平衡配置。你可以将IIS（或可能是Nginx服务器）配置为负载均衡器，并保持对连接到ASP.NET Core安装的实际服务器数量的控制。反向代理的另一个有用场景是简化SSL设置。实际上，只有反向代理才需要SSL证书。之后，可以使用纯HTTP进行与应用程序服务器的任何通信。

> ASP.NET Core的设计初衷是使用自己的HTTP服务器来确保跨多个平台的一致行为。虽然IIS，Nginx和Apache都可以用作反向代理，但每个都需要自己的环境，这需要在ASP.NET Core中构建各种=提供程序模型。因此，团队决定从ASP.NET Core中公开一个常见的独立外观，其他Web服务器可以通过一些额外的配置工作或编写其他插件来插入。
>

#### 将IIS配置为反向代理

IIS和IIS Express都可以用作ASP.NET Core的反向代理。发生这种情况时，ASP.NET Core应用程序将在与IIS工作进程分开的进程中运行。但与此同时，IIS进程需要一个专门的模块来桥接IIS工作进程和ASP.NET Core进程。这个额外的组件称为ASP.NET Core ISAPI模块。

ASP.NET Core模块负责启动ASP.NET Core应用程序并将HTTP请求转发给它。此外，它会在出入口阻止任何可能配置拒绝服务攻击的请求，或者请求体太长或可能超时的请求。此外，该模块还负责在崩溃时或IIS工作进程检测到重启条件时重新启动ASP.NET Core应用程序。

作为开发人员，您需要确保在IIS计算机上安装ASP.NET Core模块。此外，您需要在配置ASP.NET Core应用程序的主机时调用UseIISIntegration Web主机扩展方法。

#### 将Apache配置为反向代理

将Apache Web服务器配置为作为反向代理运行的确切方式取决于实际的Linux操作系统。但是，可以提供一些一般的指导。 Apache正确安装并运行后，配置文件位于/etc/httpd/conf.d/目录下。在那里创建一个扩展名为.conf的新文件，其内容类似于以下内容：

```xml
<VirtualHost *:80>
        ProxyPreserveHost On
        ProxyPass / http://127.0.0.1:5000/
        ProxyPassReverse / http://127.0.0.1:5000/
</VirtualHost>
```

在该示例中，该文件将Apache设置为侦听任何IP地址，使用端口80，以及通过机器127.0.0.1端口5000接收的所有请求。通信是双向的，因为指定了ProxyPass和ProxyPassReverse。这个步骤足以支持请求的转发，但不能让Apache管理Kestrel进程。要让Apache管理Kestrel进程，您需要创建一个服务文件，该文件是一个文本文件，它从根本上告诉Apache如何处理某些检测到的请求。

```
[Unit]
    Description=Programming ASP.NET Core Demo

[Service]
    WorkingDirectory=/var/progcore/ch14/builder
    ExecStart=/usr/local/bin/dotnet /var/progcore/ch14/builder.dll
    Restart=always

    # Restart service after 10 seconds in case of errors
    RestartSec=10
    SyslogIdentifier=progcore-ch14-builder
    User=apache
    Environment=ASPNETCORE_ENVIRONMENT=Production 

[Install]
    WantedBy=multi-user.target
```

注意，必须首先创建指定的用户（如果与apache不同），并赋予文件所有权。最后，必须从命令行启用该服务。有关详细信息，请访问https://docs.microsoft.com/en-us/aspnet/core/publishing/apache-proxy。（将Nginx配置为反向代理与此类似）

### Kestrel配置参数

在ASP.NET Core 2.0中，Kestrel的公共编程接口变得更加丰富。您现在可以轻松地将其配置为支持HTTPS，绑定到套接字和端点，以及过滤传入的请求。

#### 绑定到端点

Kestrel提供了自己的API来绑定到URL以侦听传入的请求。您配置这些端点，调用KestrelServerOptions类上的Listen方法。

```c#
var ip = "...";
var host = new WebHostBuilder()
             .UseIISIntegration() 
             .UseKestrel(options =>
             {
                options.Listen(IPAddress.Loopback, 5000);
                options.Listen(IPAddress.Parse(ip), 7000);
             });
```

Listen方法接受IPAddress类型的实例。可以通过Parse方法将任何IP地址解析为该类的实例。预定义值为本地主机的Loopback，IPv6Any是所有IPv6地址，或者任何网络地址。

在Nginx下，您还可以绑定到Unix套接字以提高性能.

```c#
var host = new WebHostBuilder()
             .UseIISIntegration() 
             .UseKestrel(options =>
             {
                options.ListenUnixSocket("/tmp/progcore-test.sock");
             });
```

最后，有三种方法可以让Kestrel知道它的侦听端点：您可以使用UseUrls扩展方法，ASPNETCORE_URLS环境变量或Listen API。 UseUrls和环境变量提供了一个非Kestrel特有的编程接口，可以与自定义（或备用）HTTP服务器一起使用。不过请注意，这些更通用的绑定方法有一些局限性。特别是，您不能在这些方法中使用SSL。此外，如果同时使用Listen API和这些方法，那么侦听端点将具有优先级。最后，请注意，如果使用IIS作为反向代理，那么IIS中硬编码的URL绑定将覆盖侦听端点和通过UseUrls或环境变量设置的端点。

#### 切换到HTTPS

要使Kestrel能够在HTTPS上运行，您只能使用Listen API指定端点。这里有一个简单的例子来说明它是如何工作的。

```c#
var host = new WebHostBuilder()
             .UseIISIntegration() 
             .UseKestrel(options =>
             {
                options.Listen(IPAddress.Loopback, 5000, listenOptions =>
                {
                   listenOptions.UseHttps("progcore.pfx");
                });
             }
          });
```

要启用HTTPS，只需向Listen方法添加第三个参数，并使用它来指示证书的路径。

#### 过滤传入的请求

在ASP.NET Core 2.0中，Kestrel Web服务器变得更强大，因为它现在支持更多配置选项，以自动过滤掉超出预设限制的传入请求。特别是，您可以设置客户端连接的最大数量，请求的最大正文大小和数据速率。

```c#
var host = new WebHostBuilder()
             .UseIISIntegration() 
             .UseKestrel(options =>
             {
                options.Limits.MaxConcurrentConnections = 100;
                options.Limits.MaxRequestBodySize = 10 * 1024;
                options.Limits.MinRequestBodyDataRate = 
                   new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
                options.Limits.MinResponseDataRate = 
                   new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
             }
          });
```

上面设置的所有设置都适用于整个应用程序的任何请求。

并发连接的数量几乎没有限制，但建议设置限制。

注意：从HTTP（或HTTPS）升级到另一个协议（可能是WebSockets）的请求不再计入并发连接总数。

默认情况下，请求的主体最大大小设置为超过3000万字节（大约28 MB）。无论设置什么默认值，都可以通过action方法上的RequestSizeLimit属性覆盖它。或者，也可以通过中间件拦截器覆盖，我们稍后会看到。

注意，Kestrel将最低数据速率设置为每秒240个字节。如果请求未发送足够的字节超过建立的宽限期（默认设置为5秒），则请求超时。

这些限制的主要原因是使Kestrel在没有反向代理保护的情况下，在面对公共互联网时能够更加健壮和更好地面对拒绝服务攻击。事实上，这些限制是针对web服务器的洪水攻击的常见防御屏障。



## ASP.NET Core 中间件

 

####   

### 编写中间件组件

 您可以使用中间件执行一些重要任务或配置要测量的环境。这是另一个例子。

```c#
app.Use(async (context, nextMiddleware) =>
{
    context.Features
        .Get<IHttpMaxRequestBodySizeFeature>()
        .MaxRequestBodySize = 10 * 1024;
    await nextMiddleware.Invoke();
});
```

这里，代码使用HTTP上下文中的信息来设置所有请求的最大正文大小。如果我们要为所有请求设置最大体型，那么我们最好在Kestrel级别上做这件事情。但是，中间件基础结构允许我们仅针对某些请求更改请求的状态。



 





 

 

  















