﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGinnBPM.MessageBus;
using System.Data.SqlClient;
using System.Data;
using System.Data.SqlTypes;
using NLog;
using System.IO;

namespace NGinnBPM.MessageBus.Impl
{
    /// <summary>
    /// Subscription database 
    /// </summary>
    public class SqlSubscriptionService : ISubscriptionService
    {
        private Logger log = LogManager.GetCurrentClassLogger();

        public SqlSubscriptionService()
        {
            SubscriptionTableName = "NGinnMessageBus_Subscriptions";
            AutoCreateSubscriptionTable = true;
            CacheExpiration = TimeSpan.FromMinutes(60); //1-hour expiration
        }

        public string ConnectionString { get; set; }

        public string SubscriptionTableName { get; set; }
        public string Endpoint { get; set; }
        public bool AutoCreateSubscriptionTable { get; set; }
        public TimeSpan CacheExpiration { get; set; }
        
        private Dictionary<string, List<string>> _cache = null;
        private DateTime _lastCacheLoad = DateTime.Now;
        private static string[] empty = new string[0];

        private IDbConnection OpenConnection()
        {
            SqlConnection con = new SqlConnection(ConnectionString);
            con.Open();
            return con;
        }

        #region ISubscriptionService Members

        protected void AccessDb(Action<IDbConnection> act)
        {
            var cn = MessageBusContext.ReceivingConnection as IDbConnection;
            if (cn != null
                && (ConnectionString == null || SqlUtil.IsSameDatabaseConnection(cn.ConnectionString, ConnectionString))
                && cn.State == ConnectionState.Open)
            {
                act(cn);
            }
            else
            {
                using (var cn2 = OpenConnection())
                {
                    act(cn2);
                }
            }
        }

        public IEnumerable<string> GetTargetEndpoints(string messageType)
        {
            List<string> lst = null;
            InitializeIfNeeded();
            if (_lastCacheLoad + CacheExpiration < DateTime.Now)
            {
                _cache = null;
            }
            var c = _cache;
            if (c == null)
            {
                AccessDb(delegate(IDbConnection con)
                {
                    c = new Dictionary<string, List<string>>();
                    using (IDbCommand cmd = con.CreateCommand())
                    {
                        cmd.CommandText = string.Format("select subscriber_endpoint, message_type from {0} where publisher_endpoint=@pub and (expiration_date is null or expiration_date >= getdate())", SubscriptionTableName);
                        SqlUtil.AddParameter(cmd, "@pub", Endpoint);

                        using (IDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                string mtype = dr.GetString(1), sub = dr.GetString(0);
                                if (!c.TryGetValue(mtype, out lst)) { lst = new List<string>(); c[mtype] = lst; }
                                if (!lst.Contains(sub)) lst.Add(sub);
                            }
                        }
                    }
                });
                lock (this)
                {
                    _cache = c;
                    _lastCacheLoad = DateTime.Now;
                }
            }
            return c.TryGetValue(messageType, out lst) ? lst : (IEnumerable<string>) empty;
        }

        public void Subscribe(string subscriberEndpoint, string messageType, DateTime? expiration)
        {
            InitializeIfNeeded();
            if (expiration.HasValue && expiration.Value < DateTime.Now) return;
            AccessDb(delegate(IDbConnection con)
            {
                using (IDbCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("update {0} set expiration_date=@expiration where publisher_endpoint=@pub and subscriber_endpoint=@sub and message_type=@mtype", SubscriptionTableName);
                    SqlUtil.AddParameter(cmd, "@pub", Endpoint);
                    SqlUtil.AddParameter(cmd, "@sub", subscriberEndpoint);
                    SqlUtil.AddParameter(cmd, "@mtype", messageType);
                    SqlUtil.AddParameter(cmd, "@expiration", expiration);

                    var rows = cmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        cmd.CommandText = string.Format("insert into {0} (publisher_endpoint, subscriber_endpoint, message_type, created_date, expiration_date) values(@pub, @sub, @mtype, getdate(), @expiration)", SubscriptionTableName);
                        cmd.Parameters.Clear();
                        SqlUtil.AddParameter(cmd, "@pub", Endpoint);
                        SqlUtil.AddParameter(cmd, "@sub", subscriberEndpoint);
                        SqlUtil.AddParameter(cmd, "@mtype", messageType);
                        SqlUtil.AddParameter(cmd, "@expiration", expiration);
                        cmd.ExecuteNonQuery();
                    }
                }
            });
            _cache = null;
        }

        public void Unsubscribe(string subscriberEndpoint, string messageType)
        {
            InitializeIfNeeded();
            AccessDb(delegate(IDbConnection con)
            {
                using (IDbCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("delete {0} where publisher_endpoint=:pub and subscriber_endpoint=:sub and message_type=:mtype", this.SubscriptionTableName);
                    SqlUtil.AddParameter(cmd, ":pub", Endpoint);
                    SqlUtil.AddParameter(cmd, ":sub", subscriberEndpoint);
                    SqlUtil.AddParameter(cmd, ":mtype", messageType);
                    cmd.ExecuteNonQuery();
                }
            });
            _cache = null;
        }

        #endregion

        protected void InitializeSubscriptionTable()
        {

            using (Stream stm = typeof(SqlSubscriptionService).Assembly.GetManifestResourceStream("NGinnBPM.MessageBus.create_subscribertable.mssql.sql"))
            {
                StreamReader sr = new StreamReader(stm);
                AccessDb(delegate(IDbConnection con)
                {
                    using (IDbCommand cmd = con.CreateCommand())
                    {
                        string txt = sr.ReadToEnd();
                        cmd.CommandText = string.Format(txt, SubscriptionTableName);
                        cmd.ExecuteNonQuery();
                    }
                });
            }
        }

        private bool _inited = false;
        protected void InitializeIfNeeded()
        {
            bool b = _inited;
            if (b) return;
            lock (this)
            {
                if (_inited) return;
                try
                {
                    if (AutoCreateSubscriptionTable)
                    {
                        InitializeSubscriptionTable();
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Error initializing subscription table: {0}", ex);
                }
                _inited = true;
            }
        }

        

        public void HandleSubscriptionExpirationIfNecessary(string subscriberEndpoint, string messageType)
        {
            InitializeIfNeeded();
            AccessDb(delegate(IDbConnection con)
            {
                using (IDbCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("delete {0} where publisher_endpoint=@pub and subscriber_endpoint=@sub and message_type=@mtype and expiration_date <= getdate()", SubscriptionTableName);
                    SqlUtil.AddParameter(cmd, "@pub", Endpoint);
                    SqlUtil.AddParameter(cmd, "@sub", subscriberEndpoint);
                    SqlUtil.AddParameter(cmd, "@mtype", messageType);

                    var rows = cmd.ExecuteNonQuery();
                    if (rows == 0) return;
                    log.Warn("Subscription expired: {0} {1}", subscriberEndpoint, messageType);
                    _cache = null;
                }

            });
        }
    }
}
