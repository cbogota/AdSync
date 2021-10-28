using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Ae.ConcurrentCollections;
using Ae.ExtranetSecurity;
using Ae.Health;
using Ae.Properties;
using Microsoft.SqlServer.Server;

namespace Ae.WebGuard.DataAccess 
{
    /// <summary>
    /// The SqlCommandCache provides a lightweight ORM that works with stored procedures. 
    /// </summary>
    public class SqlCommandCache
    {
        /// <summary>
        /// A unique name for a mapping between a given SQL command (CommentText) and an object (MapType).
        /// A mapping name is used to provide a name for mapping a parameter class to a stored procedure call as well as
        /// a mapping between a row type class and a stored procedure results set.
        /// </summary>
        private class MappingName : IEquatable<MappingName>
        {
            public readonly string CommandText;
            public readonly Type MapType;

            public bool Equals(MappingName other)
            {
                return String.CompareOrdinal(other.CommandText, CommandText) == 0 &&
                       other.MapType == MapType;
            }

            public override bool Equals(Object obj)
            {
                if (obj == null || GetType() != obj.GetType()) return false;
                return Equals((MappingName)obj);
            }

            public override int GetHashCode()
            {
                return CommandText.GetHashCode() ^ MapType.GetHashCode();
            }        
    
            public MappingName (string commandText, Type mapType)
            {
                CommandText = commandText;
                MapType = mapType;
            }
        }

        private static object MapDbNullToNull(object value)
        {
            return value == DBNull.Value ? null : value;
        }

        /// <summary>
        /// Provides a mapping between a class and a stored procedure parameter set 
        /// </summary>
        private class ParameterMapping
        {        
            // provides a mapping between a specific class field or property and an individual stored proc parameter
            private class ParameterMap
            {
                public readonly int ParameterIndex;
                public readonly bool IsInput;
                public readonly RowToClassMapping TableValuedParameterMapping;
                public readonly bool IsOutput;
                public readonly FieldInfo MapField;
                public readonly PropertyInfo MapProperty;

                private SqlDataRecord MapEnumerableCurrentItemToSqlDataRecord(object value, SqlDataRecord sdr)
                {
                    if (sdr.FieldCount == 1)
                        sdr.SetValue(0, value ?? DBNull.Value);
                    else
                        TableValuedParameterMapping.MapToSqlDataRecord(sdr, value);
                    return sdr;
                }                
                private IEnumerable<SqlDataRecord> TvpSourceMapping(object firstValue, IEnumerator remainingValues)
                {
                    var sdr = new SqlDataRecord(TableValuedParameterMapping.MetaData);
                    yield return MapEnumerableCurrentItemToSqlDataRecord(firstValue, sdr);
                    while (remainingValues.MoveNext())
                        if (remainingValues.Current != null)
                            yield return MapEnumerableCurrentItemToSqlDataRecord(remainingValues.Current, sdr);
                }
                public void MapInputParameter(SqlParameterCollection parameters, object obj)
                {
                    var sourceValue = (MapField != null
                        ? MapField.GetValue(obj)
                        : MapProperty.GetValue(obj, null));
                    if (TableValuedParameterMapping != null)
                    {
                        // ensure that we do not cause multiple enumerations of the source!
                        var sourceEnumerable = sourceValue as IEnumerable;
                        // don't set parameter for a null list
                        if (sourceEnumerable == null) return;
                        // don't set parameter for an empty list
                        var en = sourceEnumerable.GetEnumerator();
                        if (en.MoveNext())
                            parameters[ParameterIndex].SqlValue = TvpSourceMapping(en.Current, en);
                    }
                    else
                        parameters[ParameterIndex].SqlValue = sourceValue ?? DBNull.Value;
                }
                public void MapOutputParameter(SqlParameterCollection parameters, object obj)
                {
                    try
                    {
                        if (MapField != null)
                            MapField.SetValue(obj, MapDbNullToNull(parameters[ParameterIndex].Value));
                        else
                            MapProperty.SetValue(obj, MapDbNullToNull(parameters[ParameterIndex].Value), null);
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Error mapping output parameter {parameters[ParameterIndex].ParameterName} to {obj.GetType().FullName}", e);
                    }
                }

                public ParameterMap (int parameterIndex, ParameterDirection direction, RowToClassMapping tableValuedParameterMapping, MemberInfo m)
                {
                    ParameterIndex = parameterIndex;
                    IsInput = direction == ParameterDirection.InputOutput || direction == ParameterDirection.Input;
                    TableValuedParameterMapping = tableValuedParameterMapping;
                    IsOutput = direction == ParameterDirection.InputOutput || direction == ParameterDirection.Output;
                    switch (m.MemberType)
                    {
                        case MemberTypes.Field:
                            MapField = m as FieldInfo;
                            break;
                        case MemberTypes.Property:
                            MapProperty = m as PropertyInfo;
                            break;
                        default:
                            throw new ArgumentException(
                                "Only fields and properties may be mapped (m must be either a FieldInfo or PropertyInfo type)",
                                "m");
                    }
                }
            }

            public readonly MappingName MappingName;

            private readonly List<ParameterMap> _mappings;

            public readonly List<string> UnmappedParameters;

            public readonly List<string> UnmappedFieldsAndProperties;

            // members from the input instance will be used to set to set the mapped sqlparameters on the sqlcommand
            public void MapInputParameters (SqlCommand cmd, object obj)
            {
                foreach (var m in _mappings.Where(m => m.IsInput))
                    m.MapInputParameter(cmd.Parameters, obj);
            }

            // members of the output instance will be set from values in the mapped sqlparameters of the sqlcommand
            public void MapOutputParameters(SqlCommand cmd, object obj)
            {
                foreach (var m in _mappings.Where(m => m.IsOutput))
                    m.MapOutputParameter(cmd.Parameters, obj);
            }

            public static Type GetAnyElementType(Type type)
            {
                // Type is Array
                // short-circuit if you expect lots of arrays 
                if (typeof(Array).IsAssignableFrom(type))
                    return type.GetElementType();

                // type is IEnumerable<T>;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return type.GetGenericArguments()[0];

                // type implements/extends IEnumerable<T>;
                var enumType = type.GetInterfaces()
                                        .Where(t => t.IsGenericType &&
                                               t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                                        .Select(t => t.GenericTypeArguments[0]).FirstOrDefault();
                return enumType ?? type;
            }

            public ParameterMapping (SqlCommand cmd, Type t, SqlCommandCache scc)
            {
                if (cmd == null)
                    throw new ArgumentNullException("cmd");
                if (t == null)
                    throw new ArgumentNullException("t");

                MappingName = new MappingName(cmd.CommandText, t);
                _mappings = new List<ParameterMap>();

                // all sql command parameters will be put in this list and then removed as they are mapped
                UnmappedParameters = new List<string>(from SqlParameter p in cmd.Parameters select p.ParameterName);

                const BindingFlags binding = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                const MemberTypes memberTypes = MemberTypes.Field | MemberTypes.Property;

                // all object fields an properties will be put in this list and then removed as they are mapped
                UnmappedFieldsAndProperties =
                    new List<string>(t.GetMembers(binding).Where(m => memberTypes.HasFlag(memberTypes)).Select(m => m.Name));

                for (var i = 0; i < cmd.Parameters.Count; i++)
                {
                    var p = cmd.Parameters[i];
                    var foundMember = t.GetMember(p.ParameterName.TrimStart('@'), memberTypes, binding);
                    if (foundMember.Length != 1) continue;

                    RowToClassMapping tvpMapping = null;
                    if (p.SqlDbType == SqlDbType.Structured)
                    {
                        var m = foundMember[0];
                        Type enumerableType = null;
                        Type itemType = null;
                        if (m.MemberType == MemberTypes.Field && m as FieldInfo != null)
                            enumerableType = (m as FieldInfo).FieldType;
                        else if (m.MemberType == MemberTypes.Property && m as PropertyInfo != null)
                            enumerableType = (m as PropertyInfo).PropertyType;                      
                        itemType = enumerableType == null ? null : GetAnyElementType(enumerableType);
                        var tvpMappingName = new MappingName(p.TypeName, itemType);
                        if (!scc._commandResultToClassMappings.TryGetValue(tvpMappingName, out tvpMapping))
                        {
                            tvpMapping = new RowToClassMapping(tvpMappingName, scc.GetTvpDataRecord(p.TypeName));
                            scc._commandResultToClassMappings[tvpMappingName] = tvpMapping;
                        }
                    }
                    _mappings.Add(new ParameterMap(i, p.Direction, tvpMapping, foundMember[0]));
                    UnmappedParameters.Remove(p.ParameterName);
                    UnmappedFieldsAndProperties.Remove(foundMember[0].Name);
                }
            }
        }

        // Provides a mapping between a class and a result set row
        private class RowToClassMapping
        {
            private class ColumnMap
            {
                public readonly int ColumnOrdinal;
                public readonly Type ColumnType;
                public readonly FieldInfo MapField;
                public readonly PropertyInfo MapProperty;
                public ColumnMap(int resultIndex, Type columnType, MemberInfo m)
                {
                    ColumnOrdinal = resultIndex;
                    ColumnType = columnType;
                    switch (m.MemberType)
                    {
                        case MemberTypes.Field:
                            MapField = m as FieldInfo;
                            break;
                        case MemberTypes.Property:
                            MapProperty = m as PropertyInfo;
                            break;
                        default:
                            throw new ArgumentException(
                                "Only fields and properties may be mapped (m must be either a FieldInfo or PropertyInfo type)",
                                "m");
                    }
                }
            }

            public readonly SqlMetaData[] MetaData;
            public readonly MappingName MappingName;
            public readonly Type MappedType;

            private readonly List<ColumnMap> _mappings;

            public readonly List<string> UnmappedResults;

            public readonly List<string> UnmappedFieldsAndProperties;

            // members of obj will be set from values in the mapped result row
            // the altered object is also returned
            public object MapResultRow(IDataRecord rdr, object obj)
            {
                var values = new object[rdr.FieldCount];
                rdr.GetValues(values);
                foreach (var m in _mappings)
                {
                    var dbValue = MapDbNullToNull(values[m.ColumnOrdinal]);
                    if (m.MapField != null)
                    {
                        // handle mapping of database string type (or null) to a target clr type of char - nulls and empty strings map to chr(0)
                        if (m.MapField.FieldType == typeof(char) && (dbValue == null || dbValue is string))
                        {
                            var dbValueString = dbValue?.ToString();
                            m.MapField.SetValue(obj, dbValueString?.Length > 0 ? dbValueString[0] : '\0');
                        }
                        // handle special case mapping of database int type (or null) to clr bool (null, empty string, 0 or "0" will all map to false, all others map to true)
                        else if (m.MapField.FieldType == typeof(bool) && !(dbValue is bool))
                        {
                            var dbValueString = dbValue?.ToString();
                            m.MapField.SetValue(obj, !string.IsNullOrEmpty(dbValueString) && !string.Equals("0", dbValueString, StringComparison.Ordinal));
                        }
                        else
                            m.MapField.SetValue(obj, dbValue);
                    }
                    else
                    {
                        // handle mapping of database string type (or null) to a target clr type of char - nulls and empty strings map to chr(0)
                        if (m.MapProperty.PropertyType == typeof(char) && (dbValue == null || dbValue is string))
                        {
                            var dbValueString = dbValue?.ToString();
                            m.MapProperty.SetValue(obj, dbValueString?.Length > 0 ? dbValueString[0] : '\0');
                        }
                        // handle special case mapping of database int type (or null) to clr bool (null, empty string, 0 or "0" will all map to false, all others map to true)
                        else if (m.MapProperty.PropertyType == typeof(bool) && !(dbValue is bool))
                        {
                            var dbValueString = dbValue?.ToString();
                            m.MapProperty.SetValue(obj, !string.IsNullOrEmpty(dbValueString) && !string.Equals("0", dbValueString, StringComparison.Ordinal));
                        }
                        else
                            m.MapProperty.SetValue(obj, dbValue, null);
                    }
                }
                return obj;
            }

            // a new instance will be created and members set from values in the mapped result row
            // the resulting instance is then returned
            public object MapResultRow(IDataRecord rdr)
            {
                return MapResultRow(rdr, Activator.CreateInstance(MappedType));
            }

            public void MapToSqlDataRecord(SqlDataRecord sdr, object obj)
            {
                foreach (var m in _mappings)
                {
                    try
                    {
                        var value = m.MapField != null ? m.MapField.GetValue(obj) : m.MapProperty.GetValue(obj);
                        if (value != null && m.ColumnType == typeof(string) && !(value is string))
                            sdr.SetValue(m.ColumnOrdinal, value.ToString());
                        else if (m.ColumnType == typeof(byte[]) && value != null)
                            sdr.SetSqlBinary(m.ColumnOrdinal, new SqlBinary((byte[])value));
                        else
                            sdr.SetValue(m.ColumnOrdinal, value ?? DBNull.Value);
                    }
                    catch
                    {
                        Trace.WriteLine($"Incompatible mapping from {m.MapField?.ToString() ?? m.MapProperty?.ToString()} to Col:{m.ColumnOrdinal} Type:{m.ColumnType}");
                        throw;
                    }
                }
            }

            // verifies that the mapping is consistent for the provided sqldatareader
            // validates that the column index and name are still correct
            // does NOT validate if column type is still valid (eg if colum was changed from INT to BIGINT)
            public bool IsValidForSqlDataReader(IDataRecord dr)
            {

                return _mappings.All(m =>
                    m.ColumnOrdinal < dr.FieldCount && string.Equals(m.MapField?.Name ?? m.MapProperty.Name,
                        dr.GetName(m.ColumnOrdinal), StringComparison.OrdinalIgnoreCase));
            }

            public RowToClassMapping(MappingName mappingName, IDataRecord rdr)
            {
                if (rdr == null)
                    throw new ArgumentNullException("rdr");
                if (mappingName == null)
                    throw new ArgumentNullException("mappingName");

                var sdr = rdr as SqlDataRecord;
                if (sdr != null)
                {
                    MetaData = new SqlMetaData[sdr.FieldCount];
                    for (var i = 0; i < sdr.FieldCount; i++)
                        MetaData[i] = sdr.GetSqlMetaData(i);
                }
                MappingName = mappingName;
                MappedType = mappingName.MapType;
                _mappings = new List<ColumnMap>();

                UnmappedResults = new List<string>();

                const BindingFlags binding = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                const MemberTypes memberTypes = MemberTypes.Field | MemberTypes.Property;

                // all object fields an properties will be put in this list and then removed as they are mapped
                UnmappedFieldsAndProperties =
                    new List<string>(MappedType.GetMembers(binding).Where(m => memberTypes.HasFlag(memberTypes)).Select(m => m.Name));

                for (var i = 0; i < rdr.FieldCount; i++)
                {
                    var columnName = rdr.GetName(i);
                    var foundMember = MappedType.GetMember(columnName, memberTypes, binding);
                    if (foundMember.Length == 1)
                    {
                        _mappings.Add(new ColumnMap(i, rdr.GetFieldType(i), foundMember[0]));
                        UnmappedFieldsAndProperties.Remove(foundMember[0].Name);
                    }
                    else UnmappedResults.Add(columnName);
                }
            }

        }

        public readonly string ConnectionString;
        public readonly SqlConnectionStringBuilder ConnectionStringBuilder;
        private readonly string PerformanceCategoryName;
        private readonly ConcurrentDictionary<string, SqlCommand> _sqlCommandCache = new ConcurrentDictionary<string, SqlCommand>();
        private readonly ConcurrentDictionary<string, IHealthIndicator> _healthIndicators = new ConcurrentDictionary<string, IHealthIndicator>();
        
        private readonly ConcurrentDictionary<MappingName, ParameterMapping> _commandParametersToClassMappings = new ConcurrentDictionary<MappingName, ParameterMapping>();
        private readonly ConcurrentDictionary<MappingName, RowToClassMapping> _commandResultToClassMappings = new ConcurrentDictionary<MappingName, RowToClassMapping>();
        private readonly ConcurrentDictionary<string, SqlMetaData[]> _tvpSqlMetaData = new ConcurrentDictionary<string, SqlMetaData[]>();

        private IHealthIndicator GetCachedHealthIndicator(string indicatorName)
        {
            IHealthIndicator result = null;
            if (!_healthIndicators.TryGetValue(indicatorName, out result))
                _healthIndicators[indicatorName] = result = Health.Health.GetHealthIndicator(PerformanceCategoryName, indicatorName);
            return result;
        }

        private class SqlStatisticsTracker
        {
            private readonly IHealthIndicator _logicalReads;
            private readonly IHealthIndicator _physicalReads;
            public SqlStatisticsTracker(IHealthIndicator logicalReads, IHealthIndicator physicalReads)
            {
                _logicalReads = logicalReads;
                _physicalReads = physicalReads;
            }
            public void InfoMessageHandler(object sender, SqlInfoMessageEventArgs e)
            {
                if (string.IsNullOrEmpty(e.Message)) return;
                var logicalReads = e.Message.ExtractSumOfValues("logical reads ");
                var physicalReads = e.Message.ExtractSumOfValues("physical reads ");
                if (logicalReads > 0) _logicalReads.Increment(logicalReads);
                if (physicalReads > 0) _physicalReads.Increment(physicalReads);
            }
        }

        /// <summary>
        /// Executes the indicated stored procedure and maps any provided fields or properties of type T that match the name and type
        /// of a parameter in the stored procedure for both input and output.
        /// </summary>
        /// <remarks>
        /// The object passed in is returned. If a SQL error is thrown, the call is retried up to 5 times.
        /// </remarks>
        /// <param name="storedProcedureName">The stored procedure to be executed. (e.g. dbo.GetAccountById)</param>
        /// <param name="obj">An instance of type T used to provide parameter values to the stored procedure call. Must not be null.</param>
        /// <param name="timeoutSeconds">The sql command timeout value, defaults to the SqlCommandTimeout settings on the AE library settings if not specified or if set to 0</param>
        /// <typeparam name="T"> The type used to map to the stored procedure parameters. If the stored procedure only has output parameters the 
        /// caller should still pass in an instantiated object so the out parameters are set</typeparam>
        /// <returns>
        /// The same instance (of type T) that was passed in, but with any resulting output parameters set.
        /// </returns>
        public T ExecuteNonQuery<T>(string storedProcedureName, T obj, int timeoutSeconds = 0)
        {
            if (timeoutSeconds == 0)
                timeoutSeconds = Settings.Default.SqlCommandTimeout;
            IHealthIndicator healthIndicator;
            IHealthIndicator healthIndicatorRetries;
            IHealthIndicator healthIndicatorLogicalReads;
            IHealthIndicator healthIndicatorPhysicalReads;
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(storedProcedureName);
                healthIndicatorRetries = GetCachedHealthIndicator(storedProcedureName + ".Retries");
                healthIndicatorLogicalReads = GetCachedHealthIndicator(storedProcedureName + ".LogicalReads");
                healthIndicatorPhysicalReads = GetCachedHealthIndicator(storedProcedureName + ".PhysicalReads");
            }
            var timer = healthIndicator.Start();
            var retryCount = 0;
            var mappingName = new MappingName("", typeof(T));
            while (true)
            {
                try
                {
                    using (var con = new SqlConnection(ConnectionString))
                    {
                        var stats = new SqlStatisticsTracker(healthIndicatorLogicalReads, healthIndicatorPhysicalReads);
                        con.InfoMessage += stats.InfoMessageHandler;
                        using (var cmd = GetSqlCommand(storedProcedureName))
                        {
                            if (cmd.CommandTimeout != timeoutSeconds)
                                cmd.CommandTimeout = timeoutSeconds;
                            // obtain the mapping from this command to the supplied object
                            mappingName = new MappingName(cmd.CommandText, typeof (T));
                            ParameterMapping mapper;
                            _commandParametersToClassMappings.TryGetValue(mappingName, out mapper);
                            if (mapper == null)
                            {
                                mapper = new ParameterMapping(cmd, typeof (T), this);
                                _commandParametersToClassMappings[mapper.MappingName] = mapper;
                            }
                            // map the input parameters
                            mapper.MapInputParameters(cmd, obj);
                            // open the connection
                            con.Open();
                            cmd.Connection = con;
                            // execute the stored procedure
                            cmd.ExecuteNonQuery();
                            con.Close();
                            // map the output parameters
                            mapper.MapOutputParameters(cmd, obj);
                        }
                    }
                    timer.Success();
                    return obj;
                }
                catch (SqlException ex)
                {
                    //if (ex.Message.IndicatesCacheEntryShouldBeInvalidated())
                    // on second thought, in a sql exception scenario, always dump this from the cache.
                    ParameterMapping removed;
                    _commandParametersToClassMappings.TryRemove(mappingName, out removed);
                    // remove the cached commend so parameters can be rederived (maybe stored proc has been updated?)
                    SqlCommand removedCmd;
                    _sqlCommandCache.TryRemove(mappingName.CommandText, out removedCmd);
                    // do not retry if a timeout occurred
                    if (retryCount++ > Properties.Settings.Default.SqlCommandCacheMaxRetries || ex.Number == -2)
                    {
                        timer.Failure(ex);
                        throw;
                    }
                    healthIndicatorRetries.Increment();                    
                    var r = new Random();
                    Thread.Sleep(r.Next(1 + (2 << retryCount)));
                }
                catch (Exception ex)
                {
                    timer.Failure(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes the indicated stored procedure and maps any provided fields or properties of type T that match the name and type
        /// of a parameter in the stored procedure for both input and output.
        /// </summary>
        /// <remarks>
        /// The object passed in is returned. Automatic retries are not supported for this method.
        /// </remarks>
        /// <param name="storedProcedureName">The stored procedure to be executed. (e.g. dbo.GetAccountById)</param>
        /// <param name="obj">An instance of type T used to provide parameter values to the stored procedure call. Must not be null.</param>
        /// <param name="timeoutSeconds">The sql command timeout value, defaults to the SqlCommandTimeout settings on the AE library settings if not specified or if set to 0</param>
        /// <param name="completionCallback">The callback to call when the exectuion has completed (or failed)</param>
        /// <typeparam name="T"> The type used to map to the stored procedure parameters. If the stored procedure only has output parameters the 
        /// caller should still pass in an instantiated object so the out parameters are set</typeparam>
        /// <returns>
        /// The same instance (of type T) that was passed in, but with any resulting output parameters set.
        /// </returns>
        public T QueueExecuteNonQuery<T>(string storedProcedureName, T obj, Action<T, Exception> completionCallback, int timeoutSeconds = 0)
        {
            if (timeoutSeconds == 0)
                timeoutSeconds = Settings.Default.SqlCommandTimeout;
            IHealthIndicator healthIndicator;
            IHealthIndicator healthIndicatorConnect;
            //IHealthIndicator healthIndicatorLogicalReads;
            //IHealthIndicator healthIndicatorPhysicalReads;
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(storedProcedureName+".Execute");
                healthIndicatorConnect = GetCachedHealthIndicator(storedProcedureName+".Connect");
                //healthIndicatorLogicalReads = GetCachedHealthIndicator(storedProcedureName + ".LogicalReads");
                //healthIndicatorPhysicalReads = GetCachedHealthIndicator(storedProcedureName + ".PhysicalReads");
            }
            var timer = healthIndicatorConnect.Start();
            while (true)
            {
                try
                {
                    var con = new SqlConnection(ConnectionString);
                    //var stats = new SqlStatisticsTracker(healthIndicatorLogicalReads, healthIndicatorPhysicalReads);
                    //con.InfoMessage += stats.InfoMessageHandler;
                    var cmd = GetSqlCommand(storedProcedureName);
                    if (cmd.CommandTimeout != timeoutSeconds)
                        cmd.CommandTimeout = timeoutSeconds;
                    // obtain the mapping from this command to the supplied object
                    var mappingName = new MappingName(cmd.CommandText, typeof(T));
                    _commandParametersToClassMappings.TryGetValue(mappingName, out var mapper);
                    if (mapper == null)
                    {
                        mapper = new ParameterMapping(cmd, typeof(T), this);
                        _commandParametersToClassMappings[mapper.MappingName] = mapper;
                    }
                    // map the input parameters
                    mapper.MapInputParameters(cmd, obj);
                    // open the connection
                    con.Open();
                    cmd.Connection = con;
                    timer.Success();
                    timer = healthIndicator.Start();
                    // execute the stored procedure
                    cmd.BeginExecuteNonQuery(ar =>
                    {
                        Exception returnedException = null;
                        try
                        {
                            cmd.EndExecuteNonQuery(ar);
                            mapper.MapOutputParameters(cmd, obj);
                            cmd.Connection.Close();
                            cmd.Dispose();
                            con.Dispose();
                            timer.Success();
                        }
                        catch (Exception ex)
                        {
                            returnedException = ex;
                            timer.Failure(ex);
                        }
                        completionCallback(obj, returnedException);
                    }, null);
                    return obj;
                }
                catch (Exception ex)
                {
                    timer.Failure(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes the indicated stored procedure and maps any provided fields or properties of type T that match the name and type
        /// of a parameter in the stored procedure for both input and output.
        /// </summary>
        /// <remarks>
        /// The object passed in is returned. Automatic retries are not supported for this method.
        /// </remarks>
        /// <param name="storedProcedureName">The stored procedure to be executed. (e.g. dbo.GetAccountById)</param>
        /// <param name="obj">An instance of type T used to provide parameter values to the stored procedure call. Must not be null.</param>
        /// <param name="sqlConnection">An existing connection that is already open. The connection will be left open after execution.</param>
        /// <param name="timeoutSeconds">The sql command timeout value, defaults to the SqlCommandTimeout settings on the AE library settings if not specified or if set to 0</param>
        /// <param name="completionCallback">The callback to call when the exectuion has completed (or failed)</param>
        /// <typeparam name="T"> The type used to map to the stored procedure parameters. If the stored procedure only has output parameters the 
        /// caller should still pass in an instantiated object so the out parameters are set</typeparam>
        /// <returns>
        /// The same instance (of type T) that was passed in, but with any resulting output parameters set.
        /// </returns>
        public T QueueExecuteNonQueryOnOpenConnection<T>(string storedProcedureName, T obj, SqlConnection sqlConnection, Action<T, Exception> completionCallback, int timeoutSeconds = 0)
        {
            if (timeoutSeconds == 0)
                timeoutSeconds = Settings.Default.SqlCommandTimeout;
            IHealthIndicator healthIndicator;
            //IHealthIndicator healthIndicatorLogicalReads;
            //IHealthIndicator healthIndicatorPhysicalReads;
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(storedProcedureName + ".Execute");
                //healthIndicatorLogicalReads = GetCachedHealthIndicator(storedProcedureName + ".LogicalReads");
                //healthIndicatorPhysicalReads = GetCachedHealthIndicator(storedProcedureName + ".PhysicalReads");
            }
            var timer = healthIndicator.Start();
            while (true)
            {
                try
                {
                    //var stats = new SqlStatisticsTracker(healthIndicatorLogicalReads, healthIndicatorPhysicalReads);
                    //con.InfoMessage += stats.InfoMessageHandler;
                    var cmd = GetSqlCommand(storedProcedureName);
                    if (cmd.CommandTimeout != timeoutSeconds)
                        cmd.CommandTimeout = timeoutSeconds;
                    // obtain the mapping from this command to the supplied object
                    var mappingName = new MappingName(cmd.CommandText, typeof(T));
                    _commandParametersToClassMappings.TryGetValue(mappingName, out var mapper);
                    if (mapper == null)
                    {
                        mapper = new ParameterMapping(cmd, typeof(T), this);
                        _commandParametersToClassMappings[mapper.MappingName] = mapper;
                    }
                    // map the input parameters
                    mapper.MapInputParameters(cmd, obj);
                    cmd.Connection = sqlConnection;
                    // execute the stored procedure
                    cmd.BeginExecuteNonQuery(ar =>
                    {
                        Exception returnedException = null;
                        try
                        {
                            cmd.EndExecuteNonQuery(ar);
                            mapper.MapOutputParameters(cmd, obj);
                            cmd.Dispose();
                            timer.Success();
                        }
                        catch (Exception ex)
                        {
                            returnedException = ex;
                            timer.Failure(ex);
                        }
                        completionCallback(obj, returnedException);
                    }, null);
                    return obj;
                }
                catch (Exception ex)
                {
                    timer.Failure(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes the indicated stored procedure and maps any provided fields or properties of type T that match the name and type
        /// of a parameter in the storedProcedure for both input and output.
        /// </summary>
        /// <remarks>
        /// The object passed in is returned. If a SQL error is thrown, the call is retried up to 5 times.
        /// Use this method when the caller needs greater control over the processing 
        /// </remarks>
        /// <param name="storedProcedureName">The stored procedure to be executed.</param>
        /// <param name="obj">An instance of type T used to provide parameter values to the stored procedure call</param>
        /// <param name="resultProcessor">A callback routine that will be called with the resultset (SqlDataReader) 
        /// resulting from the stored procedure call.</param>
        /// <typeparam name="T">The type used to map to the stored procedure parameters (in and out)</typeparam>
        /// <returns>The same instance (of type T) that was passed in, but with any resulting output parameters set.</returns>
        public T ExecuteQuery<T>(string storedProcedureName, T obj, Action<SqlDataReader> resultProcessor)
        {
            IHealthIndicator healthIndicator;
            IHealthIndicator healthIndicatorRetries;
            IHealthIndicator healthIndicatorLogicalReads;
            IHealthIndicator healthIndicatorPhysicalReads;
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(storedProcedureName);
                healthIndicatorRetries = GetCachedHealthIndicator(storedProcedureName + ".Retries");
                healthIndicatorLogicalReads = GetCachedHealthIndicator(storedProcedureName + ".LogicalReads");
                healthIndicatorPhysicalReads = GetCachedHealthIndicator(storedProcedureName + ".PhysicalReads");
            }
            var timer = healthIndicator.Start();
            var retryCount = 0;
            var mappingName = new MappingName("", typeof(T));
            while (true)
            {
                try
                {
                    using (var con = new SqlConnection(ConnectionString))
                    {
                        var stats = new SqlStatisticsTracker(healthIndicatorLogicalReads, healthIndicatorPhysicalReads);
                        con.InfoMessage += stats.InfoMessageHandler;
                        using (var cmd = GetSqlCommand(storedProcedureName))
                        {
                            // obtain the mapping from this command to the supplied object
                            mappingName = new MappingName(cmd.CommandText, typeof(T));
                            ParameterMapping mapper;
                            _commandParametersToClassMappings.TryGetValue(mappingName, out mapper);
                            if (mapper == null)
                            {
                                mapper = new ParameterMapping(cmd, typeof(T), this);
                                _commandParametersToClassMappings[mapper.MappingName] = mapper;
                            }
                            // map the input parameters
                            mapper.MapInputParameters(cmd, obj);
                            // open the connection
                            con.Open();
                            cmd.Connection = con;
                            // execute the stored procedure
                            using (var dr = cmd.ExecuteReader())
                            {
                                resultProcessor(dr);
                                // force any remaining result sets and rows to be processed (needed to ensure the InfoMessageHandler messages are processed)
                                while (dr.NextResult()) { while (dr.Read()) { } }
                            }
                            con.Close();
                            // map the output parameters
                            mapper.MapOutputParameters(cmd, obj);
                        }
                    }
                    timer.Success();
                    return obj;
                }
                catch (SqlException ex)
                {
                    //if (ex.Message.IndicatesCacheEntryShouldBeInvalidated())
                    // on second thought, in a sql exception scenario, always dump this from the cache.
                    ParameterMapping removed;
                    _commandParametersToClassMappings.TryRemove(mappingName, out removed);
                    // remove the cached commend so parameters can be rederived (maybe stored proc has been updated?)
                    SqlCommand removedCmd;
                    _sqlCommandCache.TryRemove(mappingName.CommandText, out removedCmd);
                    if (retryCount++ > Properties.Settings.Default.SqlCommandCacheMaxRetries)
                    {
                        timer.Failure(ex);
                        throw;
                    }
                    healthIndicatorRetries.Increment();
                    var r = new Random();
                    Thread.Sleep(r.Next(1 + (2 << retryCount)));
                }
                catch (Exception ex)
                {
                    timer.Failure(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Deprecated.
        /// </summary>
        /// <param name="storedProcedureName">The name of the stored procedure that will be executed.</param>
        /// <param name="obj">The arguments that will be mapped and passed to the stored procedure.</param>
        /// <param name="dummyRow">Not used</param>
        /// <typeparam name="TParameter"></typeparam>
        /// <typeparam name="TResultRow"></typeparam>
        /// <returns></returns>
        [Obsolete("This method will be removed in a future release. Instead use: IEnumerable<TResultRow> ExecuteQuery<TParameter, TResultRow>(string storedProcedureName, TParameter obj)")]
        public IEnumerable<TResultRow> ExecuteQuery<TParameter, TResultRow>(string storedProcedureName, TParameter obj, TResultRow dummyRow) where TResultRow : new()
        {
            return ExecuteQuery<TParameter, TResultRow>(storedProcedureName, obj);
        }

        /// <summary>
        /// Executes the indicated stored procedure and maps any provided fields or properties of type T that match the name and type
        /// of a parameter in the storedProcedure for both input and output. 
        /// </summary>
        /// <typeparam name="TParameter">The type used to map to the stored procedure parameters (in and out)</typeparam>
        /// <typeparam name="TResultRow">Must have a default constructor. This the type that each record in the result set will be mapped to.</typeparam>
        /// <param name="storedProcedureName">The name of the stored procedure that will be executed.</param>
        /// <param name="obj">The arguments that will be mapped and passed to the stored procedure.</param>
        /// <returns>Returns an IEnumerable of type TResultRow. TResult properties and fields are mapped from the result set of the stored procedure.</returns>
        public IEnumerable<TResultRow> ExecuteQuery<TParameter, TResultRow>(string storedProcedureName, TParameter obj) where TResultRow : new()
        {
            IHealthIndicator healthIndicator;
            IHealthIndicator healthIndicatorLogicalReads;
            IHealthIndicator healthIndicatorPhysicalReads;
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(storedProcedureName);
                healthIndicatorLogicalReads = GetCachedHealthIndicator(storedProcedureName + ".LogicalReads");
                healthIndicatorPhysicalReads = GetCachedHealthIndicator(storedProcedureName + ".PhysicalReads");
            }
            var timer = healthIndicator.Start();
            var errorState = "Connecting";
            SqlException sqlExceptionThrown = null;
            try
            {
                using (var con = new SqlConnection(ConnectionString))
                {
                    var stats = new SqlStatisticsTracker(healthIndicatorLogicalReads, healthIndicatorPhysicalReads);
                    con.InfoMessage += stats.InfoMessageHandler;
                    errorState = "Getting Command";
                    using (var cmd = GetSqlCommand(storedProcedureName))
                    {
                        errorState = "Getting Parameter Mapping";
                        var mappingName = new MappingName(cmd.CommandText, typeof (TParameter));
                        ParameterMapping mapper;
                        _commandParametersToClassMappings.TryGetValue(mappingName, out mapper);
                        if (mapper == null)
                        {
                            mapper = new ParameterMapping(cmd, typeof (TParameter), this);
                            _commandParametersToClassMappings[mapper.MappingName] = mapper;
                        }
                        errorState = "Mapping Input Parameters";
                        mapper.MapInputParameters(cmd, obj);
                        errorState = "Opening Connection";
                        con.Open();
                        cmd.Connection = con;
                        SqlDataReader rdr = null;
                        try
                        {
                            errorState = "Executing Command";
                            try
                            {
                                rdr = cmd.ExecuteReader();
                            }
                            catch (SqlException ex)
                            {
                                sqlExceptionThrown = ex;
                                timer.Failure(ex);
                                throw;
                            }

                            RowToClassMapping resultMapper = null;
                            errorState = "Reading First Row";
                            while (rdr.Read())
                            {
                                if (resultMapper == null)
                                {
                                    errorState = "Getting Result Mapping";
                                    // Bugfix: this was trying to look up the result mapping using the parameter MappingName, so it never found it and always computed remapping on every call
                                    var resultMappingName = new MappingName(cmd.CommandText, typeof(TResultRow));
                                    _commandResultToClassMappings.TryGetValue(resultMappingName, out resultMapper);
                                    if (resultMapper == null)
                                    {
                                        resultMapper = new RowToClassMapping(resultMappingName, rdr);
                                        _commandResultToClassMappings[resultMapper.MappingName] = resultMapper;
                                    }
                                }

                                errorState = "Mapping Result";
                                var row = (TResultRow) resultMapper.MapResultRow(rdr, new TResultRow());
                                errorState = null;
                                yield return row;
                                errorState = "Reading Next Row";
                            }

                            // force any remaining result sets and rows to be processed (needed to ensure the InfoMessageHandler messages are processed)
                            while (rdr.NextResult())
                            {
                                while (rdr.Read())
                                {
                                }
                            }

                            errorState = null;
                        }
                        finally
                        {
                            rdr?.Dispose();
                            if (sqlExceptionThrown == null && errorState == null)
                            {
                                errorState = "Mapping Output Parameters";
                                mapper.MapOutputParameters(cmd, obj);
                                errorState = null;
                            }

                            errorState = null;

                        }
                    }
                }
            }
            finally
            {
                if (sqlExceptionThrown == null)
                {
                    if (errorState == null)
                        timer.Success();
                    else
                        timer.Failure(new Exception(errorState));
                }
            }
        }

        /// <summary>
        /// MapResultSet enables mapping of result set rows to a class for stored procedures that return multiple result sets.
        /// </summary>
        /// <remarks>This method can be used along with <see cref="ExecuteQuery{T}(string, T, Action{SqlDataReader})"/> 
        /// Using the ExecuteQuery method to return the data reader then calling this method multiple times to map
        /// each resultset to IEnumerable{TResultRow}</remarks>
        /// <typeparam name="TResultRow">The class that will be mapped to the result row</typeparam>
        /// <param name="dr">The datareader to read rows from. The datareader will be read until no more rows are available.
        /// dr.NextResult() will be called when all of the rows have been read.</param>
        /// <param name="storedProcedureName">The name of the stored procedure that generated the result set.</param>
        /// <param name="resultSetIndex">When mapping the first result set, 1 should be passed in, followed by 2 for the 
        /// second result set and so on.</param>
        /// <returns>An IEnumberable of the class type that was mapped into. </returns>
        public IEnumerable<TResultRow> MapResultSet<TResultRow>(SqlDataReader dr, string storedProcedureName,
            int resultSetIndex) where TResultRow : new()
        {
            IHealthIndicator healthIndicator;
            RowToClassMapping resultMapper = null;
            string resultSetName = $"{storedProcedureName}.{resultSetIndex}";
            //TODO: remove unecessary locks around GetCachedHealthIndicator...
            lock (_healthIndicators)
            {
                healthIndicator = GetCachedHealthIndicator(resultSetName);
            }
            var timer = healthIndicator.Start();

            var errorState = "Reading First Row";
            try
            {
                while (dr.Read())
                {
                    if (resultMapper == null)
                    {
                        errorState = "Getting Result Mapping";
                        var resultSetMappingName = new MappingName(resultSetName, typeof (TResultRow));
                        _commandResultToClassMappings.TryGetValue(resultSetMappingName, out resultMapper);
                        // TODO: validate the cached mapping befo4re using (create new method bool RowToClassMapping.IsValid(SqlDataReader dr)) and recreate if no longer valid
                        if (resultMapper == null || !resultMapper.IsValidForSqlDataReader(dr))
                        {
                            resultMapper = new RowToClassMapping(resultSetMappingName, dr);
                            _commandResultToClassMappings[resultMapper.MappingName] = resultMapper;
                        }
                    }
                    errorState = "Mapping Result";
                    var row = (TResultRow) resultMapper.MapResultRow(dr, new TResultRow());
                    errorState = null;
                    yield return row;
                    errorState = "Reading Next Row";
                }
                // force skip to next result set (needed to ensure the InfoMessageHandler messages are processed)
                errorState = "Calling NextResult";
                dr.NextResult();
                errorState = null;
            }
            finally
            {
                if (errorState == null)
                    timer.Success();
                else
                    timer.Failure(new Exception($"{errorState} - {resultSetName}"));
            }
        }

        ///// <summary>
        ///// MapResultSet enables mapping of result set rows to a class for stored procedures that return multiple result sets.
        ///// </summary>
        ///// <remarks>This method can be used along with <see cref="ExecuteQuery{T}(string, T, Action{SqlDataReader})"/> 
        ///// Using the ExecuteQuery method to return the data reader then calling this method multiple times to map
        ///// each resultset to IEnumerable{TResultRow}</remarks>
        ///// <typeparam name="TResultRow">The class that will be mapped to the result row</typeparam>
        ///// <param name="dr">The datareader to read rows from. The datareader will be read until no more rows are available.
        ///// dr.NextResult() will be called when all of the rows have been read.</param>
        ///// <param name="storedProcedureName">The name of the stored procedure that generated the result set.</param>
        ///// <param name="resultSetName">Each result set should be given a unique name</param>
        ///// <returns>An IEnumberable of the class type that was mapped into. </returns>
        //public void MapResultSet<TResultRowKey, TResultRow>(SqlDataReader dr, string storedProcedureName,
        //        string resultSetName, ConcurrentDictionary<TResultRowKey, TResultRow> dictionary)
        //        where TResultRowKey : IEqualityComparer<TResultRowKey>
        //        where TResultRow : IValueSnapshot<TResultRowKey>, new()
        //{
        //    IHealthIndicator healthIndicator;
        //    RowToClassMapping resultMapper = null;
        //    string indicatorName = $"{storedProcedureName}.{resultSetName}";
        //    healthIndicator = GetCachedHealthIndicator(indicatorName);
        //    var timer = healthIndicator.Start();

        //    var errorState = "Reading First Row";
        //    try
        //    {
        //        while (dr.Read())
        //        {
        //            if (resultMapper == null)
        //            {
        //                errorState = "Getting Result Mapping";
        //                var resultSetMappingName = new MappingName(indicatorName, typeof(TResultRow));
        //                _commandResultToClassMappings.TryGetValue(resultSetMappingName, out resultMapper);
        //                if (resultMapper == null)
        //                {
        //                    resultMapper = new RowToClassMapping(resultSetMappingName, dr);
        //                    _commandResultToClassMappings[resultMapper.MappingName] = resultMapper;
        //                }
        //            }
        //            errorState = "Mapping Result";
        //            var row = (TResultRow)resultMapper.MapResultRow(dr, new TResultRow());
        //            errorState = null;
        //            //yield return row;
        //            errorState = "Reading Next Row";
        //        }
        //        // force skip to next result set (needed to ensure the InfoMessageHandler messages are processed)
        //        errorState = "Calling NextResult";
        //        dr.NextResult();
        //        errorState = null;
        //    }
        //    finally
        //    {
        //        if (errorState == null)
        //            timer.Success();
        //        else
        //            timer.Failure(new Exception($"{errorState} - {indicatorName}"));
        //    }
        //}

        /// <summary>
        /// This method will generate a SqlCommand by deriving the parameters by calling the stored procedure. 
        /// </summary>
        /// <remarks>The connection property is NOT set: the caller must handle acquiring, assigning and disposing the connection as needed.</remarks>
        /// <param name="storedProcedureName">The name of the stored procedure to generate a SqlCommand for.</param>
        /// <returns>Returns a SqlCommand instance with a fully populated parameters collection</returns>
        private SqlCommand GetSqlCommand(string storedProcedureName)
        {
            SqlCommand cachedCmd;
            _sqlCommandCache.TryGetValue(storedProcedureName, out cachedCmd);
            if (cachedCmd == null)
            {
                using (var con = new SqlConnection(ConnectionString))
                {
                    con.Open();
                    using (var cmd = new SqlCommand(storedProcedureName, con))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        SqlCommandBuilder.DeriveParameters(cmd);
                        con.Close();
                        // because this cmd will be used as a template for cloning, we don't want a connection on it
                        cmd.Connection = null;
                        // deal with issue identified in comment "Table valued parameters not typed correctly"
                        // from: http://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlcommandbuilder.deriveparameters.aspx
                        foreach (var p in from SqlParameter p in cmd.Parameters where p.SqlDbType == SqlDbType.Structured && p.TypeName.IndexOf('.') != p.TypeName.LastIndexOf('.') select p)
                            p.TypeName = p.TypeName.Substring(p.TypeName.IndexOf(".", StringComparison.Ordinal) + 1);
                        cmd.CommandTimeout = Settings.Default.SqlCommandTimeout;
                        // we are going to cache a clone of the cmd, because this cmd will be disposed once we leave the using block
                        _sqlCommandCache[storedProcedureName] = cachedCmd = cmd.Clone();
                    }
                }
            }
            // return a clone of the cmd for the caller to work with
            return cachedCmd.Clone();
        }

        private static SqlMetaData ParseSqlMetaData(SqlDataReader schemaRow)
        {
            //var columnOrdinal = (string)schemaRow["ColumnOrdinal"];
            var columnName = (string) schemaRow["ColumnName"];
            var columnSize = (int) schemaRow["ColumnSize"];
            var dataTypeName = (string) schemaRow["DataTypeName"];
            var sqlDataType = (SqlDbType) Enum.Parse(typeof (SqlDbType), dataTypeName, true);
            switch (sqlDataType)
            {
                case SqlDbType.VarChar:
                case SqlDbType.NVarChar:
                case SqlDbType.NChar:
                case SqlDbType.Char:
                case SqlDbType.Binary:
                case SqlDbType.VarBinary:
                    if (columnSize > 8000)
                    {
                        columnSize = -1;
                    }
                    return new SqlMetaData(columnName, sqlDataType, columnSize);
                case SqlDbType.NText:
                case SqlDbType.Text:
                    return new SqlMetaData(columnName, sqlDataType, -1);
                default:
                    return new SqlMetaData(columnName, sqlDataType);
            }
        }

        private readonly ConcurrentDictionary<string, SqlMetaData[]> _tvpMetadata = new ConcurrentDictionary<string, SqlMetaData[]>();
        private SqlDataRecord GetTvpDataRecord(string tableTypeName)
        {
            SqlMetaData[] metadata;
            if (_tvpMetadata.TryGetValue(tableTypeName, out metadata))
                return new SqlDataRecord(metadata);
            const string queryText =
                "SELECT CAST(ROW_NUMBER() OVER (PARTITION BY tt.schema_id, tt.name ORDER BY c.column_id) - 1 AS INT) AS ColumnOrdinal, " +
                " c.name AS ColumnName, st.name AS DataTypeName, CAST(CASE WHEN st.name = 'NVARCHAR' AND c.max_length > 0 THEN c.max_length /2 ELSE c.max_length END AS INT) AS ColumnSize, " +
                " c.precision AS Precision, c.scale AS Scale, c.is_nullable AS IsNullable, " +
                " CAST(COUNT(*) OVER (PARTITION BY tt.schema_id, tt.name) AS INT) AS ColumnCount " + 
                "FROM sys.table_types tt join sys.columns c ON c.object_id = tt.type_table_object_id " +
                " JOIN sys.types AS ST ON ST.user_type_id = c.user_type_id " +
                "WHERE SCHEMA_NAME(tt.schema_id) + '.' + tt.name = @tableTypeName " +
                "ORDER BY 1;";
            using (var con = new SqlConnection(ConnectionString))
            {
                con.Open();
                using (var cmd = new SqlCommand(queryText, con))
                {
                    var tvpParam = cmd.Parameters.AddWithValue("@tableTypeName", tableTypeName);
                    tvpParam.SqlDbType = SqlDbType.NVarChar;
                    tvpParam.Size = 1024;
                    cmd.CommandType = CommandType.Text;
                    using (var dr = cmd.ExecuteReader())
                        while (dr.Read())
                        {
                            metadata = metadata ?? new SqlMetaData[(int)dr["ColumnCount"]];
                            metadata[(int)dr["ColumnOrdinal"]] = ParseSqlMetaData(dr);
                        }
                    _tvpMetadata[tableTypeName] = metadata;
                }
            }
            return metadata == null ? null : new SqlDataRecord(metadata);           
        }

        /// <summary>
        /// Creates an instance of the SqlCommandCache. 
        /// </summary>
        /// <remarks>It is expected that the caller will hold the instance for the lifetime of the application to maintain the cached
        /// stored procedure to entity mappings</remarks>
        /// <param name="connectionString">The database connection string that will be used on all database calls.</param>
        /// <param name="performanceCategoryName">The performance category name that will be used by the AE.Health library for tracking performance counters.</param>
        public SqlCommandCache(string connectionString, string performanceCategoryName)
        {
            ConnectionString = connectionString;
            ConnectionStringBuilder = new SqlConnectionStringBuilder(connectionString);
            PerformanceCategoryName = performanceCategoryName;
        }
    }
}
