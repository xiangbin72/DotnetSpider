﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using DotnetSpider.Core;
using DotnetSpider.Core.Infrastructure;
using DotnetSpider.Extension.Model;
using System.Collections.Concurrent;
using NLog;
using DotnetSpider.Core.Redial;
using DotnetSpider.Extension.Infrastructure;
using System.Configuration;
using DotnetSpider.Core.Infrastructure.Database;
using DotnetSpider.Core.Pipeline;

namespace DotnetSpider.Extension.Pipeline
{
	public abstract class BaseEntityDbPipeline : BaseEntityPipeline
	{
		private ConnectionStringSettings _connectionStringSettings;
		private readonly string _connectString;

		protected abstract ConnectionStringSettings CreateConnectionStringSettings(string connectString = null);
		protected abstract string GenerateInsertSql(EntityAdapter adapter);
		protected abstract string GenerateUpdateSql(EntityAdapter adapter);
		protected abstract string GenerateSelectSql(EntityAdapter adapter);
		protected abstract string GenerateCreateTableSql(EntityAdapter adapter);
		protected abstract string GenerateCreateDatabaseSql(EntityAdapter adapter, string serverVersion);
		protected abstract string GenerateIfDatabaseExistsSql(EntityAdapter adapter, string serverVersion);
		protected abstract DbParameter CreateDbParameter(string name, object value);

		internal ConcurrentDictionary<string, EntityAdapter> EntityAdapters { get; set; } = new ConcurrentDictionary<string, EntityAdapter>();

		public IUpdateConnectString UpdateConnectString { get; set; }

		public ConnectionStringSettings ConnectionStringSettings
		{
			get
			{
				if (null == _connectionStringSettings)
				{
					if (string.IsNullOrEmpty(_connectString))
					{
						if (null == Env.DataConnectionStringSettings)
						{
							throw new SpiderException("Default DbConnection unfound.");
						}
						else
						{
							_connectionStringSettings = CreateConnectionStringSettings(Env.DataConnectionStringSettings?.ConnectionString);
						}
					}
					else
					{
						_connectionStringSettings = CreateConnectionStringSettings(_connectString);
					}
				}

				return _connectionStringSettings;
			}
			set => _connectionStringSettings = value;
		}

		public bool CheckIfSameBeforeUpdate { get; set; }

		protected BaseEntityDbPipeline(string connectString = null, bool checkIfSaveBeforeUpdate = false)
		{
			_connectString = connectString;
			CheckIfSameBeforeUpdate = checkIfSaveBeforeUpdate;
		}

		public override void AddEntity(EntityDefine entityDefine)
		{
			if (entityDefine == null)
			{
				throw new ArgumentException("Should not add a null entity to a entity dabase pipeline.");
			}

			if (entityDefine.TableInfo == null)
			{
				Logger.MyLog(Spider?.Identity, $"Schema is necessary, Skip {GetType().Name} for {entityDefine.Name}.", LogLevel.Warn);
				return;
			}

			EntityAdapter entityAdapter = new EntityAdapter(entityDefine.TableInfo, entityDefine.Columns);

			if (entityAdapter.Table.UpdateColumns != null && entityAdapter.Table.UpdateColumns.Length > 0)
			{
				entityAdapter.SelectSql = GenerateSelectSql(entityAdapter);
				entityAdapter.UpdateSql = GenerateUpdateSql(entityAdapter);
				entityAdapter.InsertModel = false;
			}

			entityAdapter.InsertSql = GenerateInsertSql(entityAdapter);
			EntityAdapters.TryAdd(entityDefine.Name, entityAdapter);
		}

		public override void InitPipeline(ISpider spider)
		{
			if (ConnectionStringSettings == null)
			{
				if (UpdateConnectString == null)
				{
					throw new SpiderException("ConnectionStringSettings or IUpdateConnectString are unfound.");
				}
				else
				{
					for (int i = 0; i < 5; ++i)
					{
						try
						{
							ConnectionStringSettings = UpdateConnectString.GetNew();
							break;
						}
						catch (Exception e)
						{
							Logger.MyLog(Spider.Identity, "Update ConnectString failed.", LogLevel.Error, e);
							Thread.Sleep(1000);
						}
					}

					if (ConnectionStringSettings == null)
					{
						throw new SpiderException("Can not update ConnectionStringSettings via IUpdateConnectString.");
					}
				}
			}

			base.InitPipeline(spider);

			InitDatabaseAndTable();
		}

		internal void InitDatabaseAndTable()
		{
			foreach (var adapter in EntityAdapters.Values)
			{
				if (!adapter.InsertModel)
				{
					continue;
				}
				using (var conn = ConnectionStringSettings.GetDbConnection())
				{
					var sql = GenerateIfDatabaseExistsSql(adapter, conn.ServerVersion);

					if (Convert.ToInt16(conn.MyExecuteScalar(sql)) == 0)
					{
						sql = GenerateCreateDatabaseSql(adapter, conn.ServerVersion);
						conn.MyExecute(sql);
					}

					sql = GenerateCreateTableSql(adapter);
					conn.MyExecute(sql);
				}
			}
		}

		public override int Process(string entityName, List<DataObject> datas)
		{
			if (datas == null || datas.Count == 0)
			{
				return 0;
			}
			int count = 0;
			if (EntityAdapters.TryGetValue(entityName, out var metadata))
			{
				using (var conn = ConnectionStringSettings.GetDbConnection())
				{
					if (metadata.InsertModel)
					{
						count += conn.MyExecute(metadata.InsertSql, datas);
					}
					else
					{
						count += conn.MyExecute(metadata.UpdateSql, datas);
					}
				}
			}
			return count;
		}

		public static IPipeline GetPipelineFromAppConfig()
		{
			if (Env.DataConnectionStringSettings == null)
			{
				return null;
			}
			IPipeline pipeline;
			switch (Env.DataConnectionStringSettings.ProviderName)
			{
				case "Npgsql":
					{
						pipeline = new PostgreSqlEntityPipeline();
						break;
					}
				case "MySql.Data.MySqlClient":
					{
						pipeline = new MySqlEntityPipeline();
						break;
					}
				case "System.Data.SqlClient":
					{
						pipeline = new SqlServerEntityPipeline();
						break;
					}
				case "MongoDB":
					{
						pipeline = new MongoDbEntityPipeline(Env.DataConnectionString);
						break;
					}
				default:
					{
						pipeline = new NullPipeline();
						break;
					}
			}
			return pipeline;
		}

		/// <summary>
		/// For test
		/// </summary>
		/// <returns></returns>
		public string[] GetUpdateColumns(string entityName)
		{
			if (EntityAdapters.TryGetValue(entityName, out var metadata))
			{
				return metadata.Table.UpdateColumns;
			}
			return null;
		}
	}
}

