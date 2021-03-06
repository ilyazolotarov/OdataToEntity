﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OdataToEntity.AspNetCore;
using OdataToEntity.EfCore;
using OdataToEntity.Test;

namespace OdataToEntity.AspServer
{
    public class Startup
    {
        private Db.OeDataAdapter _dataAdapter;

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();
            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvcCore();

            _dataAdapter = new OrderOeDataAdapter(Test.Model.OrderContext.GenerateDatabaseName());
            services.AddSingleton(typeof(Db.OeDataAdapter), _dataAdapter);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            app.UseOdataToEntityMiddleware("/api", _dataAdapter, _dataAdapter.BuildEdmModelFromEfCoreModel());
            app.UseMvcWithDefaultRoute();
        }
    }

}
