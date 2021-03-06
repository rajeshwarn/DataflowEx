﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Gridsum.DataflowEx.Databases
{
    using System.Collections;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using Common.Logging;

    /// <summary>
    /// Global manager for type accessors
    /// </summary>
    public class TypeAccessorManager<T> where T : class
    {
        private static readonly ConcurrentDictionary<TargetTable, Lazy<TypeAccessor<T>>> s_accessors;

        static TypeAccessorManager()
        {
            s_accessors = new ConcurrentDictionary<TargetTable, Lazy<TypeAccessor<T>>>();
        }

        private TypeAccessorManager()
        {
        }

        /// <summary>
        /// If the typeAccessor exists, just return it; else create a new one with parameter: destLabel, connectionString,
        /// dataTableName
        /// </summary>
        public static TypeAccessor<T> GetAccessorForTable(TargetTable target)
        {
            return s_accessors.GetOrAdd(target, t => new Lazy<TypeAccessor<T>>(() => new TypeAccessor<T>(t))).Value;
        }
    }

    /// <summary>
    /// Allows external configs to be registered and used by TypeAccessors.
    /// </summary>
    public class TypeAccessorConfig
    {
        internal static List<Tuple<Expression, DBColumnMapping>> s_externalMappings = new List<Tuple<Expression, DBColumnMapping>>();

        private static void RegisterMapping(Expression propertyPath, DBColumnMapping mapping)
        {
            s_externalMappings.Add(Tuple.Create(propertyPath, mapping));
        }

        /// <summary>
        /// Register an external mapping to affect the column mapping process by type accessors.
        /// </summary>
        /// <typeparam name="T">Type of the root object</typeparam>
        /// <typeparam name="TValue">Return type of the leaf property</typeparam>
        /// <param name="propertyPath">A lambda expression which defines the property path from root type to the leaf property</param>
        /// <param name="mapping">The mapping to register to the give property path</param>
        public static void RegisterMapping<T, TValue>(Expression<Func<T, TValue>> propertyPath, DBColumnMapping mapping) where T : class
        {
            //todo: add lambda expression check
            RegisterMapping(propertyPath.Body, mapping);
        }

        internal static bool ExpressionPathEquals(Expression e1, Expression e2)
        {
            if (e1 is ParameterExpression && e2 is ParameterExpression)
            {
                return e1.Type == e2.Type;
            }

            if (e1 is MemberExpression && e2 is MemberExpression)
            {
                MemberExpression m1 = e1 as MemberExpression;
                MemberExpression m2 = e2 as MemberExpression;
                
                if (m1.Member.Equals(m2.Member))
                {
                    return ExpressionPathEquals(m1.Expression, m2.Expression);
                }
                else
                {
                    return false;
                }
            }

            return false;
        }
    }

    public class TypeAccessor<T> where T : class
    {
        private readonly string m_connectionString;
        private readonly IList<DBColumnMapping> m_dbColumnMappings;
        private readonly string m_destinationTablename;
        private readonly string m_destLabel;
        private readonly Dictionary<int, Func<T, object>> m_properties;
        private readonly ILog m_classLogger;
        private Lazy<DataTable> m_schemaTable;

        #region ctor and init

        public TypeAccessor(TargetTable target)
        {
            m_destLabel = target.DestLabel;
            m_connectionString = target.ConnectionString;
            m_destinationTablename = string.IsNullOrWhiteSpace(target.TableName)
                ? typeof (T).Name
                : target.TableName;
            m_schemaTable = new Lazy<DataTable>(this.GetSchemaTable);
            m_properties = new Dictionary<int, Func<T, object>>();
            m_dbColumnMappings = new List<DBColumnMapping>();
            m_classLogger = LogManager.GetLogger(this.GetType().Namespace + "." + this.GetType().GetFriendlyName()); 
            CreateTypeVisitor();
        }

        private void CreateTypeVisitor()
        {
            var rootNode = new RootNode<T>();
            var mappings = this.RecursiveGetAllMappings(rootNode);
            
            foreach (DBColumnMapping mapping in mappings)
            {
                m_dbColumnMappings.Add(mapping);

                Expression<Func<T, object>> lambda =
                    Expression.Lambda<Func<T, object>>(
                        Expression.Convert(mapping.Host.CreatePropertyAccessorExpression(mapping.DefaultValue),typeof(object)),
                        rootNode.RootParam);

                m_properties.Add(mapping.DestColumnOffset, lambda.Compile());
            }
        }

        public DataTable SchemaTable
        {
            get
            {
                return m_schemaTable.Value;
            }
        }

        private DataTable GetSchemaTable()
        {
            DataTable schemaTable = null;
            if (string.IsNullOrWhiteSpace(m_connectionString))
            {
                LogHelper.Logger.Warn("connection string is null or empty, so database table can not be found.");
                schemaTable = new DataTable();
            }
            if (schemaTable != null) return schemaTable;
            using (var conn = new SqlConnection(m_connectionString))
            {
                schemaTable = new DataTable();
                new SqlDataAdapter(string.Format("SELECT * FROM {0}", m_destinationTablename), conn).FillSchema(
                    schemaTable, SchemaType.Source);
            }
            return schemaTable;
        }
        
        /// <summary>
        ///     递归获得该类型的所有被选择的属性。
        ///     如果出现一个值类型或String添加了相应DestLabel的DbColumnMapping。则选取所有的带DbColumnMapping的属性。
        ///     否则选取所有值类型或String。
        ///     再利用与数据库进行匹配，对应到相应的列。如果出现多属性对应同一个列，则报错。
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private IList<DBColumnMapping> RecursiveGetAllMappings(RootNode<T> root)
        {
            //所有的值类型
            var leafs = new List<LeafPropertyNode>();

            #region 读取所有的引用类型、值类型及String类型的属性

            var typeExpandQueue = new Queue<ExpressionTreeNode>();
            typeExpandQueue.Enqueue(root);
            
            while (typeExpandQueue.Count > 0)
            {
                var nodeToExpand = typeExpandQueue.Dequeue();
                Type currentType = nodeToExpand.ResultType;

                if (!nodeToExpand.IsExpandable)
                {
                    LogHelper.Logger.DebugFormat("{0} ({1}) is not expandable. Ignore it.", nodeToExpand, currentType.GetFriendlyName());
                    continue;
                }

                if (currentType.IsAbstract || currentType.IsInterface)
                {
                    LogHelper.Logger.WarnFormat("Expanding properties for interface or abstract class type: {0}", currentType.GetFriendlyName());
                }

                foreach (PropertyInfo prop in currentType.GetProperties())
                {
                    //值类型或引用类型
                    if (PropertyTreeNode.IsLeafNodeType(prop.PropertyType))
                    {
                        leafs.Add(new LeafPropertyNode(prop, nodeToExpand, m_destLabel));
                    }
                    else
                    {
                        var nonLeaf = new NonLeafPropertyNode(prop, nodeToExpand);

                        if (nonLeaf.HasReferenceLoop)
                        {
                            m_classLogger.WarnFormat("Type reference loop found on {0}. Ignore this property path.", nonLeaf);
                        }
                        else
                        {
                            typeExpandQueue.Enqueue(nonLeaf);
                        }
                    }
                }
            }
            #endregion



            //Check and complete DBColumnMapping 
            foreach (LeafPropertyNode leafNode in leafs)
            {
                foreach (DBColumnMapping mapping in leafNode.DbColumnMappings)
                {
                    this.PopulateDbColumnMapping(leafNode, mapping);
                }

                //filter ignored column mappings
                leafNode.DbColumnMappings = leafNode.DbColumnMappings.Where(m => m.IsDestColumnOffsetOk()).ToList();
            }

            List<LeafPropertyNode> mappedLeafs = leafs.Where(_ => _.DbColumnMappings.Count > 0).ToList();

            if (mappedLeafs.Count == 0)
            {
                //create mapping from property name, our last try
                this.AutoCreateDBColumnMapping(leafs);
                mappedLeafs = leafs.Where(_ => _.DbColumnMappings.Count > 0).ToList();
                if (mappedLeafs.Count == 0)
                {
                    throw new InvalidOperationException("No valid db column mapping found for type " + typeof(T).GetFriendlyName());
                }
            }

            return this.DeduplicateDbColumnMappingByOffset(mappedLeafs);
        }

        private void AutoCreateDBColumnMapping(IList<LeafPropertyNode> leafNodes)
        {
            //there isn't property with DestLabel attribute,so we can get it from database table.
            LogHelper.Logger.WarnFormat(
                "Mapping property by schema table for current type: {0}, which has no attribute on each of its properties.",
                typeof(T));

            #region 没有属性存在DbColumnMapping。因此，采用属性名称匹配数据库

            DataTable dataTable = this.SchemaTable;

            foreach (DataColumn column in dataTable.Columns)
            {
                if (column == null || column.ReadOnly)
                {
                    continue;
                }

                IEnumerable<LeafPropertyNode> matchedLeafs =
                    leafNodes.Where(
                        t => string.Equals(t.PropertyInfo.Name, column.ColumnName, StringComparison.OrdinalIgnoreCase));

                foreach (LeafPropertyNode leaf in matchedLeafs)
                {
                    var dbMapping = new DBColumnMapping(this.m_destLabel, column.Ordinal, null)
                                        {
                                            DestColumnName = column.ColumnName
                                        };

                    dbMapping.Host = leaf;
                    leaf.DbColumnMappings.Add(dbMapping);
                }
            }

            #endregion
        }

        /// <summary>
        ///     说明：此方法用于利用数据库表的列字段的：位置或名称，将DbColumnMapping补全。
        ///     如果输入的propertyInfo.PropertyType不是“原子类型”或“String”，显然在数据库中不会有匹配的列；所以直接返回
        /// </summary>
        /// <param name="propertyInfo"></param>
        /// <param name="mapping"></param>
        private void PopulateDbColumnMapping(LeafPropertyNode leaf, DBColumnMapping mapping)
        {
            DataTable schemaTable = this.SchemaTable;

            if (mapping.IsDestColumnNameOk() && mapping.IsDestColumnOffsetOk())
            {
                DataColumn col = schemaTable.Columns[mapping.DestColumnOffset];

                if (col == null)
                {
                    if (mapping.Option == ColumnMappingOption.Optional)
                    {
                        m_classLogger.DebugFormat("Optional column mapping ignored for table {0}: {1}", m_destinationTablename, mapping);
                        mapping.DestColumnOffset = -1;
                        return;
                    }
                    else
                    {
                        var desc = string.Format(
                            "can not find column with offset {0} in table {1} ",
                            mapping.DestColumnOffset,
                            m_destinationTablename);

                        throw new InvalidDBColumnMappingException(desc, mapping, leaf);
                    }
                }

                if (col.ColumnName != mapping.DestColumnName)
                {
                    var desc = string.Format(
                            "Column name from db {0} is inconsistent with that in db mapping {1} ",
                            col.ColumnName,
                            mapping);

                    throw new InvalidDBColumnMappingException(desc, mapping, leaf);
                }

                return;
            }

            //说明当前的mapping的列名称出错（null），而位置参数正确。则读取数据库表获得要相应的列名称
            if (!mapping.IsDestColumnNameOk() && mapping.IsDestColumnOffsetOk())
            {
                DataColumn col = schemaTable.Columns[mapping.DestColumnOffset];
                if (col == null)
                {
                    if (mapping.Option == ColumnMappingOption.Optional)
                    {
                        m_classLogger.DebugFormat("Optional column mapping ignored for table {0}: {1}", m_destinationTablename, mapping);
                        mapping.DestColumnOffset = -1;
                        return;
                    }
                    else
                    {
                        var desc = string.Format(
                            "can not find column with offset {0} in table {1} ",
                            mapping.DestColumnOffset,
                            m_destinationTablename);

                        throw new InvalidDBColumnMappingException(desc, mapping, leaf);
                    }
                }
                
                mapping.DestColumnName = col.ColumnName;
                this.m_classLogger.DebugFormat("Populated column name for DBColumnMapping: {0} on property node: {1} by table {2}",
                        mapping,
                        leaf,
                        m_destinationTablename);
                return;
            }

            //说明当前的mapping的列名称存在，而位置参数出错（-1）。则读取数据库表获得相应的列位置参数
            if (mapping.IsDestColumnNameOk() && !mapping.IsDestColumnOffsetOk())
            {
                DataColumn col = schemaTable.Columns[mapping.DestColumnName];
                if (col == null)
                {
                    if (mapping.Option == ColumnMappingOption.Optional)
                    {
                        m_classLogger.DebugFormat(
                            "Optional column mapping ignored for table {0}: {1}",
                            m_destinationTablename,
                            mapping);
                        mapping.DestColumnOffset = -1;
                        return;
                    }
                    else
                    {
                        var desc = string.Format(
                            "can not find column with name {0} in table {1} ",
                            mapping.DestColumnName,
                            m_destinationTablename);

                        throw new InvalidDBColumnMappingException(desc, mapping, leaf);
                    }
                }

                mapping.DestColumnOffset = col.Ordinal;
                this.m_classLogger.DebugFormat("Populated column offset for DBColumnMapping: {0} on property node: {1} by table {2}",
                        mapping,
                        leaf,
                        m_destinationTablename);
                return;
            }

            //说明当前的mapping列名称不存在，位置参数也不存在，因此，根据PropertyInfo.Name读取数据库
            DataColumn guessColumn = schemaTable.Columns[leaf.PropertyInfo.Name];
            if (guessColumn == null)
            {
                if (mapping.Option == ColumnMappingOption.Optional)
                {
                    m_classLogger.DebugFormat("Optional column mapping ignored for table {0}: {1}", m_destinationTablename, mapping);
                    mapping.DestColumnOffset = -1;
                    return;
                }
                else
                {
                    var desc = string.Format(
                            "can not find column with property name {0} in table {1} ",
                            leaf.PropertyInfo.Name,
                            m_destinationTablename);

                    throw new InvalidDBColumnMappingException(desc, mapping, leaf);
                }
            }
            mapping.DestColumnOffset = guessColumn.Ordinal;
            mapping.DestColumnName = guessColumn.ColumnName;

            this.m_classLogger.DebugFormat("Populated column name and offset for DBColumnMapping: {0} on property node: {1} by table {2}",
                        mapping,
                        leaf,
                        m_destinationTablename);
        }

        /// <summary>
        ///     利用DbColumn的信息，去除匹配到相同列的多余属性，只选择其中一个。选择的规定为：
        ///     1、当只有一个属性匹配时，则选择之；
        ///     2、当有多个属性匹配，则深度不同时，选择深度最小者。
        ///     如A.B.C.D, A.B.D。则选择后者
        ///     3、如果有多个属性匹配，且最小深度有多个，则默认选择第一个。
        /// </summary>
        /// <returns></returns>
        private IList<DBColumnMapping> DeduplicateDbColumnMappingByOffset(IList<LeafPropertyNode> leafs)
        {
            var filtered = new List<DBColumnMapping>();
            foreach (var group in leafs
                .SelectMany(l => l.DbColumnMappings)
                .GroupBy(m => m.DestColumnOffset))
            {
                //规则一
                if (group.Count() == 1)
                {
                    filtered.Add(group.First());
                }
                else
                {
                    //规则二、三
                    DBColumnMapping selected = group.OrderBy(t => t.Host.Depth * 10 + (int)t.Option).First();
                    filtered.Add(selected);

                    foreach (var mapping in group)
                    {
                        if (!object.ReferenceEquals(mapping, selected))
                        {
                            m_classLogger.WarnFormat("Column mapping {0} on {1} abandoned as its offset {2} is already used by {3}", mapping, mapping.Host, mapping.DestColumnOffset, selected);
                        }
                    }
                }
            }
            return filtered;
        }

        #endregion

        #region instance public properties, methods

        public string CurrentDestLabel
        {
            get { return m_destLabel; }
        }

        public string ConnectionString
        {
            get { return m_connectionString; }
        }

        public string DestinationTableName
        {
            get { return m_destinationTablename; }
        }

        public List<SqlBulkCopyColumnMapping> ColumnMappings
        {
            get { return m_properties.Keys.Select(prop => new SqlBulkCopyColumnMapping(prop, prop)).ToList(); }
        }

        public int FieldCount
        {
            get { return m_properties.Count; }
        }

        public Func<T, object> GetPropertyAccessor(int columnOffset)
        {
            return m_properties[columnOffset];
        }

        public string GetName(int columnOffset)
        {
            DBColumnMapping columnAttr = m_dbColumnMappings.FirstOrDefault(col => col.DestColumnOffset == columnOffset);
            return columnAttr == null ? null : columnAttr.DestColumnName;
        }

        public int GetColumnOffset(string name)
        {
            DBColumnMapping columnAttr =
                m_dbColumnMappings.FirstOrDefault(
                    col => string.Equals(col.DestColumnName, name, StringComparison.OrdinalIgnoreCase));
            return columnAttr == null ? -1 : columnAttr.DestColumnOffset;
        }

        public DBColumnMapping GetColumnMapping(int colId)
        {
            return m_dbColumnMappings.FirstOrDefault(b => b.DestColumnOffset == colId);
        }

        public ImmutableList<DBColumnMapping> DbColumnMappings
        {
            get
            {
                return m_dbColumnMappings.ToImmutableList();
            }
        }
        #endregion
    }
    
    public abstract class ExpressionTreeNode
    {
        public abstract Type ResultType { get; }
        public ExpressionTreeNode Parent { get; set; }
        public int Depth { get; set; }

        public ExpressionTreeNode(ExpressionTreeNode parent)
        {
            this.Parent = parent;

            if (parent == null)
            {
                Depth = 0;
            }
            else
            {
                Depth = parent.Depth + 1;            
            }            
        }

        public bool HasReferenceLoop
        {
            get
            {
                var node = this.Parent;
                while (node != null)
                {
                    if (this.ResultType == node.ResultType)
                    {
                        return true;
                    }

                    node = node.Parent;
                }
                return false;
            }
        }

        public ExpressionTreeNode Root
        {
            get
            {
                ExpressionTreeNode node = this;
                while (node.Parent != null)
                {
                    node = node.Parent;
                }
                return node;
            }
        }

        public abstract bool IsExpandable { get; }
        public abstract Expression Expression { get; }
        public abstract bool NoNullCheck { get; }

        //Expression without null check and default value involvement
        public abstract Expression RawExpression { get; }
    }

    public abstract class PropertyTreeNode : ExpressionTreeNode
    {
        private Type m_resultType;
        public PropertyInfo PropertyInfo { get; set; }
                
        public PropertyTreeNode(PropertyInfo propertyInfo, ExpressionTreeNode parent) : base(parent)
        {
            this.PropertyInfo = propertyInfo;
            m_resultType = propertyInfo.PropertyType;

            this.DbColumnMappings = new List<DBColumnMapping>((DBColumnMapping[])propertyInfo.GetCustomAttributes(typeof(DBColumnMapping), true));

            foreach (var dbColumnMapping in DbColumnMappings)
            {
                dbColumnMapping.Host = this;
            }
        }
        
        public static bool IsLeafNodeType(Type type)
        {
            return type.IsValueType || type == typeof(string) || type == typeof(byte[]);
        }
        
        public override string ToString()
        {
            if (Parent == null)
            {
                return this.ResultType.GetFriendlyName();
            }
            else
            {
                return string.Format("{0}->{1}", Parent, PropertyInfo.Name);
            }
        }

        public List<DBColumnMapping> DbColumnMappings { get; set; }
        
        /// <summary>
        /// Create property access expression with a default value
        /// </summary>
        /// <returns>An expression representing the value of the property node</returns>
        internal Expression CreatePropertyAccessorExpression(object defaultValue)
        {
            PropertyInfo prop = this.PropertyInfo;
            Type propType = prop.PropertyType;

            if (defaultValue == null)
            {
                return this.CreatePropertyAccessorExpression();
            }
            if (propType.IsValueType && !propType.IsNullableType()) //Normal value type cannot have a default value
            {
                return this.CreatePropertyAccessorExpression();
            }

            Expression nullExpr = Expression.Constant(null);
            ParameterExpression localVarExpr = Expression.Variable(prop.PropertyType, "tmp");
            Expression defaultValExpr = Expression.Constant(defaultValue, prop.PropertyType);

            //now: only class type (string) or nullable with a non-null default value
            if (this.Parent.NoNullCheck)
            {
                // tmp = parent-expression.P;
                // if (tmp == null)
                //    tmp = default
                // return tmp;
                var code1 = Expression.Assign(localVarExpr, Expression.Property(this.Parent.Expression, prop));
                var code2 = Expression.IfThen(
                    Expression.Equal(localVarExpr, nullExpr),
                    Expression.Assign(localVarExpr, defaultValExpr));

                //返回值
                LabelTarget labelTarget = Expression.Label(prop.PropertyType);
                GotoExpression retExpr = Expression.Return(labelTarget, localVarExpr);
                LabelExpression labelExpr = Expression.Label(labelTarget, localVarExpr);
                BlockExpression block = Expression.Block(new[] { localVarExpr }, code1, code2, retExpr, labelExpr);
                return block;
            }
            else
            {
                //p = {parent expression};
                ParameterExpression localParentVarExpr = Expression.Variable(this.Parent.ResultType);
                var code1 = Expression.Assign(localParentVarExpr, this.Parent.Expression);
                //if (p != null)
                //{
                //   tmp = p.P;
                //   if (tmp != null)
                //      return tmp;
                //   else
                //      return defaultValue;
                //}
                //else
                //{
                //   return defaultValue;
                //}
                LabelTarget labelTarget = Expression.Label(prop.PropertyType);
                LabelExpression labelExpr = Expression.Label(labelTarget, localVarExpr);
                var code2 = Expression.IfThenElse(
                    Expression.NotEqual(localParentVarExpr, nullExpr),
                    Expression.Block(
                        new[] { localVarExpr },
                        Expression.Assign(localVarExpr, Expression.Property(localParentVarExpr, prop)),
                        Expression.IfThenElse(
                            Expression.NotEqual(localVarExpr, nullExpr),
                            Expression.Return(labelTarget, localVarExpr),
                            Expression.Return(labelTarget, defaultValExpr))),
                    Expression.Return(labelTarget, defaultValExpr));

                return Expression.Block(new[] { localVarExpr, localParentVarExpr }, code1, code2, labelExpr);
            }
        }

        /// <summary>
        /// Create property access expression without a default value
        /// </summary>
        /// <returns>An expression representing the value of the property node</returns>
        protected virtual Expression CreatePropertyAccessorExpression()
        {
            PropertyInfo prop = this.PropertyInfo;
            ParameterExpression localParentVarExpr = Expression.Variable(this.Parent.ResultType);

            BinaryExpression ifParentNotNull = Expression.NotEqual(localParentVarExpr, Expression.Constant(null));
            MemberExpression propExpr = Expression.Property(localParentVarExpr, prop);
            ParameterExpression localVarExpr = Expression.Variable(prop.PropertyType);

            if (this.Parent.NoNullCheck)
            {
                return Expression.Property(this.Parent.Expression, prop);
            }
            else
            {
                Expression defaultExpression;
                if (prop.PropertyType.IsValueType && !prop.PropertyType.IsNullableType()) //value type
                {
                    defaultExpression = Expression.Constant(Activator.CreateInstance(prop.PropertyType));
                }
                else
                {
                    defaultExpression = Expression.Constant(null, prop.PropertyType);
                }

                //p = {parent expression};
                //if (p != null)
                //{
                //  return p.P;
                //}
                //else
                //{
                //  return default(T);
                //}
                LabelTarget labelTarget = Expression.Label(prop.PropertyType);
                LabelExpression labelExpr = Expression.Label(labelTarget, localVarExpr);

                Expression assignConditionally = Expression.IfThenElse(
                    ifParentNotNull,
                    Expression.Return(labelTarget, propExpr),
                    Expression.Return(labelTarget, defaultExpression));

                BlockExpression block = Expression.Block(
                    new[] { localVarExpr, localParentVarExpr },
                    Expression.Assign(localParentVarExpr, this.Parent.Expression),
                    assignConditionally,
                    labelExpr
                    );
                return block;
            }
        }

        public override Type ResultType
        {
            get { return m_resultType; }
        }
        
        public override Expression RawExpression
        {
            get
            {
                return Expression.Property(this.Parent.RawExpression, this.PropertyInfo);
            }
        }

        public override bool IsExpandable
        {
            get
            {
                if (typeof(IEnumerable).IsAssignableFrom(this.ResultType))
                {
                    return false;
                }

                var paths = (DBColumnPath[])PropertyInfo.GetCustomAttributes(typeof(DBColumnPath), true);

                if (paths.Any(p => p.HasOption(DBColumnPathOptions.DoNotExpand)))
                {
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// 用于存放一个Property在当前的类结构中的深度及表达式
    /// </summary>
    /// <remarks>
    /// Middle node in property tree
    /// </remarks>
    public class NonLeafPropertyNode : PropertyTreeNode
    {
        private readonly Lazy<Expression> m_exprIniter;
        private readonly bool m_noNullCheck;
        private static ILog s_logger = LogManager.GetLogger(typeof(NonLeafPropertyNode).Namespace);

        public NonLeafPropertyNode(PropertyInfo propertyInfo, ExpressionTreeNode parent)
            : base(propertyInfo, parent)
        {
            this.m_exprIniter = new Lazy<Expression>(this.CreatePropertyAccessorExpression);
            m_noNullCheck = propertyInfo.GetCustomAttributes(typeof(NoNullCheckAttribute), true).Any()
                | propertyInfo.GetCustomAttributes(typeof(DBColumnPath), true).Cast<DBColumnPath>()
                .Any(p => p.HasOption(DBColumnPathOptions.DoNotGenerateNullCheck));

            if (m_noNullCheck && s_logger.IsTraceEnabled)
            {
                s_logger.TraceFormat("No null check is enabled on {0}", this);
            }
        }
        
        public override Expression Expression
        {
            get
            {
                return this.m_exprIniter.Value;
            }
        }

        public override bool NoNullCheck
        {
            get
            {
                return m_noNullCheck;
            }
        }
    }

    public class RootNode<T> : ExpressionTreeNode
    {
        private ParameterExpression m_param;

        public RootNode() : base(null)
        {
            m_param = Expression.Parameter(typeof(T), "t");
        }

        public override bool IsExpandable
        {
            get
            {
                return !typeof(IEnumerable).IsAssignableFrom(this.ResultType);
            }
        }

        public override Type ResultType {
            get
            {
                return typeof(T);
            }
        }
        
        public override Expression Expression
        {
            get
            {
                return m_param;
            }
        }

        public override bool NoNullCheck
        {
            get
            {
                return true;
            }
        }

        public override Expression RawExpression
        {
            get
            {
                return m_param;
            }
        }

        public PropertyTreeNode Parent
        {
            get
            {
                return null;
            }
        }

        public ParameterExpression RootParam
        {
            get
            {
                return m_param;
            }
        }

        public override string ToString()
        {
            return this.ResultType.GetFriendlyName();
        }
    }

    /// <summary>
    /// 用于存放一个值类型或String类型的DbColumnMapping
    /// </summary>
    /// <remarks>
    /// Leaf node in property tree
    /// </remarks>
    public class LeafPropertyNode : PropertyTreeNode
    {
        public LeafPropertyNode(PropertyInfo propertyInfo, ExpressionTreeNode parent, string destLabel)
            : base(propertyInfo, parent)
        {
            var externalMappings =
                TypeAccessorConfig.s_externalMappings.Where(pair => pair.Item2.DestLabel == destLabel)
                    .Where(pair => TypeAccessorConfig.ExpressionPathEquals(pair.Item1, this.RawExpression))
                    .Select(pair => pair.Item2)
                    .ToList();

            foreach (var externalMapping in externalMappings)
            {
                externalMapping.Host = this;
            }

            DbColumnMappings = DbColumnMappings.Where(m => m.DestLabel == destLabel).Union(externalMappings).ToList();

            //type safety check for db column mappings in order to fail early rather than insertion time
            foreach (var dbColumnMapping in DbColumnMappings)
            {
                if (dbColumnMapping.DefaultValue != null)
                {
                    if (this.ResultType.IsValueType)
                    {
                        Type innerType;
                        if (this.ResultType.IsNullableType(out innerType))
                        {
                            if (innerType.IsInstanceOfType(dbColumnMapping.DefaultValue))
                            {
                                //conversion here
                                //dbColumnMapping.DefaultValue = Convert.ChangeType(dbColumnMapping.DefaultValue,this.ResultType);
                            }
                            else
                            {
                                throw new InvalidDBColumnMappingException("The default value has wrong type",dbColumnMapping,this);
                            }
                        }
                        else
                        {
                            throw new InvalidDBColumnMappingException(
                                "A value type should not have non-null default value. If you want one consider declare the property type as Nullable<T>."
                                , dbColumnMapping, this);
                        }
                    }
                    else if (this.ResultType == typeof(string))
                    {
                        if (! (dbColumnMapping.DefaultValue is string))
                        {
                            throw new InvalidDBColumnMappingException("The default value has wrong type", dbColumnMapping, this);
                        }
                    }
                    else if (this.ResultType == typeof(byte[]))
                    {
                        if (! (dbColumnMapping.DefaultValue is byte[]))
                        {
                            throw new InvalidDBColumnMappingException("The default value has wrong type", dbColumnMapping, this);
                        }
                    }
                    else
                    {
                        Debug.Fail("Should not reach here. A leaf node should not have ResultType as class type");
                    }
                }
            }
        }

        public override Expression Expression
        {
            get
            {
                throw new NotSupportedException();
                //object defaultValue;
                //defaultValue = this.DbColumnMappings.Select(_ => _.DefaultValue).Distinct().Single();
                //return this.GetExpressionWithDefaultVal(defaultValue);
            }
        }

        public override bool IsExpandable
        {
            get { return false; }
        }

        public override bool NoNullCheck
        {
            get { return true; }
        }
    }
}