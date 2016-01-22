﻿// GraphView
// 
// Copyright (c) 2015 Microsoft Corporation
// 
// All rights reserved. 
// 
// MIT License
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace GraphView
{
    /// <summary>
    /// When an edge in the MATCH clause is not given an alias, this edge can still be referenced 
    /// by the edge column name. During translation, an edge without an explicit alias will be
    /// assigned a default alias, and as a result, the edge column name must be replaced by 
    /// the assigned alias. 
    /// </summary>
    internal class ReplaceEdgeReferenceVisitor : WSqlFragmentVisitor
    {
        /// <summary>
        /// Edge column name-> List of the candidate edge alias
        /// </summary>
        private Dictionary<string, List<string>> _edgeTableReferenceDict;

        public void Invoke(WSqlFragment node, Dictionary<string, List<string>> edgeTableReferenceDict)
        {
            _edgeTableReferenceDict = edgeTableReferenceDict;
            node.Accept(this);
        }

        public override void Visit(WColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier == null)
                return;
            var column = node.MultiPartIdentifier.Identifiers;
            if (column.Count >= 2)
            {
                var columnName = column[column.Count - 2].Value;
                if (_edgeTableReferenceDict.ContainsKey(columnName))
                {
                    if (_edgeTableReferenceDict[columnName].Count > 1)
                        throw new GraphViewException("Ambiguious Table Reference");
                    column[column.Count - 2].Value = _edgeTableReferenceDict[columnName].First();
                }
            }
            
            base.Visit(node);
        }
    }

    /// <summary>
    /// When a table in the FROM clause is not given an alias, this table will be assigned its 
    /// table name as its alias, and all references with schema name corresponding to this table
    /// should be replaced with the assigned alias by removing the schema name since it is invalid
    /// to have a schema identifier before an alias.
    /// </summary>
    internal class RemoveSchemanameInIdentifersVisitor : WSqlFragmentVisitor
    {
        public override void Visit(WSelectStarExpression node)
        {
            if (node.Qulifier != null && node.Qulifier.Count > 1)
                node.Qulifier = new WMultiPartIdentifier(node.Qulifier.Identifiers.Last());
        }

        public override void Visit(WColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier == null)
            {
                return;
            }
            var column = node.MultiPartIdentifier.Identifiers;
            var number = column.Count;
            if (number >= 3)
            {
                var tableName = column[number - 2];
                var columName = column[number - 1];
                column.Clear();
                column.Insert(0, tableName);
                column.Insert(1, columName);
            }
        }
    }

    public class NodeColumns
    {
        public WNodeTableColumnRole Role;
        public EdgeInfo EdgeInfo;
    }

    public class EdgeInfo
    {
        public HashSet<string> SinkNodes;
        public List<Tuple<string, string>> EdgeColumns;
        public IList<string> ColumnAttributes;
    }

    public class GraphMetaData
    {
        // Columns of each node table. For edge columns, edge attributes are attached.
        // (Schema name, Table name) -> (Column name -> Column Info)
        public readonly Dictionary<Tuple<string, string>, Dictionary<string, NodeColumns>> ColumnsOfNodeTables =
            new Dictionary<Tuple<string, string>, Dictionary<string, NodeColumns>>();

        // Node tables included in the node view.
        // (Schema name, Table name) -> set of the node table name included in the node view
        public readonly Dictionary<Tuple<string, string>, HashSet<string>> NodeViewMapping =
            new Dictionary<Tuple<string, string>, HashSet<string>>();

    }

    /// <summary>
    /// Translate match clause and add it to the from clause.
    /// Check validity -> Divide the graph into connected sub-graph -> Retrive the estimation
    /// -> Use DP-like algorithem to get the component -> add the component(s) into from clause
    /// </summary>
    internal class TranslateMatchClauseVisitor : WSqlFragmentVisitor
    {
        private WSqlTableContext _context;

        // A list of variables defined in a GraphView script and used in a SELECT statement.
        // When translating a GraphView SELECT statement, the optimizer sends a T-SQL SELECT query
        // to the SQL engine to estimate table cardinalities. The variables must be defined 
        // at the outset so that the SQL engine is able to parse and estimate the T-SQL SELECT query 
        // successfully. 
        private IList<DeclareVariableElement> _variables;

        // Upper Bound of the State number
        private const int MaxStates =
            //1000;
            100;
        //5000;
        //8000;
        //10000;
        //int.MaxValue;



        private GraphMetaData _graphMetaData;

        // Set Selectivity Calculation Method
        private readonly IMatchJoinStatisticsCalculator _statisticsCalculator = new HistogramCalculator();

        // Set Pruning Strategy
        private readonly IMatchJoinPruning _pruningStrategy = new PruneJointEdge();

        public SqlTransaction Tx { get; private set; }

        public TranslateMatchClauseVisitor(SqlTransaction tx)
        {
            this.Tx = tx;
            Init();
        }

        /// <summary>
        /// Retrieve the metadata
        /// </summary>
        /// <param name="conn"></param>
        private void Init()
        {
            _graphMetaData = new GraphMetaData();
            var columnsOfNodeTables = _graphMetaData.ColumnsOfNodeTables;
            var nodeViewMapping = _graphMetaData.NodeViewMapping;

            using (var command = Tx.Connection.CreateCommand())
            {
                command.Transaction = Tx;
                command.CommandText = string.Format(
                    @"
                    SELECT [TableSchema] as [Schema], [TableName] as [Name1], [ColumnName] as [Name2], 
                           [ColumnRole] as [Role], [Reference] as [Name3], null as [EdgeViewTable], null as [ColumnId]
                    FROM [{0}]
                    UNION ALL
                    SELECT [TableSchema] as [Schema], [TableName] as [Name1], [ColumnName] as [Name2], 
                           -1 as [Role], [AttributeName] as [Name3], null, [AttributeId]
                    FROM [{1}]
                    UNION ALL
                    SELECT [NV].[TableSchema] as [Schema], [NV].[TableName] as [Name1], [NT].[TableName] as [Name2], 
                           -2 as [Role], null as [Name3], null, null
                    FROM 
                        [{2}] as [NV_NT_Mapping]
                        JOIN
                        [{3}] as [NV]
                        ON NV_NT_Mapping.NodeViewTableId = NV.TableId
                        JOIN 
                        [{3}] as [NT]
                        ON NV_NT_Mapping.TableId = NT.TableId
                    UNION ALL
                    SELECT [EV].[TableSchema] as [Schema], [EV].[ColumnName] as [Name1], [ED].[ColumnName]as [Name2],
                           -3 as [Role], [ED].[TableName] as [Name3], [EV].[TableName] as [EdgeViewTable], [ED].[ColumnId] as [ColumnId]
                    FROM 
                        [{4}] as [EV_ED_Mapping]
                        JOIN
                        [{0}] as [EV]
                        ON [EV_ED_Mapping].[NodeViewColumnId] = [EV].[ColumnId] and [EV].[ColumnRole] = 3
                        JOIN
                        [{0}] as [ED]
                        ON [EV_ED_Mapping].[ColumnId] = [ED].[ColumnId]
                        ORDER BY [ColumnId]", GraphViewConnection.MetadataTables[1],
                    GraphViewConnection.MetadataTables[2], GraphViewConnection.MetadataTables[7],
                    GraphViewConnection.MetadataTables[0], GraphViewConnection.MetadataTables[5]);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int tag = (int) reader["Role"];
                        string schema = reader["Schema"].ToString().ToLower(CultureInfo.CurrentCulture);
                        string name1 = reader["Name1"].ToString().ToLower(CultureInfo.CurrentCulture);
                        string name2 = reader["Name2"].ToString().ToLower(CultureInfo.CurrentCulture);
                        // Retrieve columns of node tables
                        var tableTuple = new Tuple<string, string>(schema, name1);
                        if (tag >= 0)
                        {
                            Dictionary<string, NodeColumns> columnDict;
                            if (!columnsOfNodeTables.TryGetValue(tableTuple, out columnDict))
                            {
                                columnDict = new Dictionary<string, NodeColumns>(StringComparer.OrdinalIgnoreCase);
                                columnsOfNodeTables.Add(tableTuple, columnDict);
                            }
                            var role = (WNodeTableColumnRole) tag;
                            EdgeInfo edgeInfo = null;
                            // Edge column
                            if (role == WNodeTableColumnRole.Edge || role == WNodeTableColumnRole.EdgeView)
                            {
                                edgeInfo = new EdgeInfo
                                {
                                    ColumnAttributes = new List<string>(),
                                    SinkNodes = role == WNodeTableColumnRole.Edge
                                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                        {
                                            reader["Name3"].ToString().ToLower(CultureInfo.CurrentCulture)
                                        }
                                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                };

                            }
                            columnDict.Add(name2,
                                new NodeColumns
                                {
                                    EdgeInfo = edgeInfo,
                                    Role = role,
                                });
                        }
                        // Retrieve edge attributes
                        else if (tag == -1)
                        {
                            var columnDict = columnsOfNodeTables[tableTuple];
                            columnDict[name2].EdgeInfo.ColumnAttributes.Add(reader["Name3"].ToString()
                                .ToLower(CultureInfo.CurrentCulture));
                        }
                        // Retrieve node view mapping
                        else if (tag == -2)
                        {
                            HashSet<string> nodeTableSet;
                            if (!nodeViewMapping.TryGetValue(tableTuple, out nodeTableSet))
                            {
                                nodeTableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                nodeViewMapping.Add(tableTuple, nodeTableSet);
                            }
                            nodeTableSet.Add(name2);
                        }
                        // Retrieve edge view mapping
                        else if (tag == -3)
                        {
                            string edgeViewSourceTableName =
                                reader["EdgeViewTable"].ToString().ToLower(CultureInfo.CurrentCulture);
                            string sourceTableName = reader["Name3"].ToString().ToLower(CultureInfo.CurrentCulture);
                            string sinkTableName =
                                columnsOfNodeTables[new Tuple<string, string>(schema, sourceTableName)][name2]
                                    .EdgeInfo.SinkNodes.First();
                            var edgeViewInfo =
                                columnsOfNodeTables[new Tuple<string, string>(schema, edgeViewSourceTableName)][
                                    name1].EdgeInfo;

                            if (!edgeViewInfo.SinkNodes.Contains(sourceTableName))
                                edgeViewInfo.SinkNodes.Add(sinkTableName);
                            if (edgeViewInfo.EdgeColumns == null)
                                edgeViewInfo.EdgeColumns = new List<Tuple<string, string>>();
                            edgeViewInfo.EdgeColumns.Add(new Tuple<string, string>(sourceTableName, name2));
                        }
                    }

                }
            }
        }

        public void Invoke(WSqlFragment node)
        {
            node.Accept(this);
        }

        /// <summary>
        /// Uses the union-Find algorithm to decompose the graph pattern into fully-connected components
        /// </summary>
        private class UnionFind
        {
            public Dictionary<string, string> Parent;

            public string Find(string x)
            {
                string k, j, r;
                r = x;
                while (Parent[r] != r)
                {
                    r = Parent[r];
                }
                k = x;
                while (k != r)
                {
                    j = Parent[k];
                    Parent[k] = r;
                    k = j;
                }
                return r;
            }

            public void Union(string a, string b)
            {
                string aRoot = Find(a);
                string bRoot = Find(b);
                if (aRoot == bRoot)
                    return;
                Parent[aRoot] = bRoot;
            }
        }

        /// <summary>
        /// Checks whether a table reference in the FROM clause is a node table. 
        /// In GraphView's SELECT statement, a table reference in the FROM clause 
        /// could also be a regular table. 
        /// </summary>
        /// <param name="table">The table reference in the FROM clause</param>
        /// <returns>True if the table reference is a node table; otherwise, false.</returns>
        private bool IsNodeTable(WTableReferenceWithAlias table)
        {
            var namedTable = table as WNamedTableReference;
            if (namedTable == null)
                return false;
            var tableschema = namedTable.TableObjectName.SchemaIdentifier != null
                ? namedTable.TableObjectName.SchemaIdentifier.Value
                : "dbo";
            var tableName = namedTable.TableObjectName.BaseIdentifier.Value;
            return
                _graphMetaData.ColumnsOfNodeTables.Keys.Contains(
                    new Tuple<string, string>(tableschema.ToLower(CultureInfo.CurrentCulture),
                        tableName.ToLower(CultureInfo.CurrentCulture)));
        }


        /// <summary>
        /// Checks the validity of the MATCH clause, including 
        /// (1) an edge is bound to a node table or node view, 
        /// (2) the source and the sink of an edeg in a path expression are bound to corresponding node tables, 
        ///     as specified when node tables are created, and 
        /// (3) The length constraint for a path construct is valid 
        /// </summary>
        /// <param name="node">True, if the MATCH clause passes the test; false, otherwise.</param>
        private void CheckValidity(WSelectQueryBlock node)
        {
            if (node.MatchClause == null)
                return;
            // Checks validity of the source node/node view
            if (node.MatchClause.Paths.All(
                path => path.PathEdgeList.All(
                    part => _context.CheckTable(part.Item1.BaseIdentifier.Value) &&
                            IsNodeTable(_context[part.Item1.BaseIdentifier.Value])
                    )
                ))
            {
                foreach (var path in node.MatchClause.Paths)
                {
                    var index = 0;
                    for (var count = path.PathEdgeList.Count; index < count; ++index)
                    {
                        var pathNode = path.PathEdgeList[index];
                        var table = _context[pathNode.Item1.BaseIdentifier.Value] as WNamedTableReference;
                        var edgeCol = pathNode.Item2;
                        var edge =
                            edgeCol.MultiPartIdentifier.Identifiers.Last().Value.ToLower();
                        var nodeTableTuple = WNamedTableReference.SchemaNameToTuple(table.TableObjectName);
                        var schema = nodeTableTuple.Item1;

                        // Binds edge/edge view to node/node view and check validity
                        string bindNode = _context.BindEdgeToNode(schema, edge, nodeTableTuple.Item2, _graphMetaData);
                        if (string.IsNullOrEmpty(bindNode))
                            throw new GraphViewException(string.Format("Edge/EdgeView {0} cannot be bind to {1}.{2}",
                                edge,
                                nodeTableTuple.Item1, nodeTableTuple.Item2));

                        // Check edge length
                        if (edgeCol.MinLength<0)
                            throw new GraphViewException(
                                string.Format(
                                    "The minimal length of the path {0} should be non-negative integer",
                                    edge));
                        if (edgeCol.MaxLength!=-1 && edgeCol.MinLength > edgeCol.MaxLength)
                            throw new GraphViewException(
                                string.Format(
                                    "The minimal length of the path {0} should not be larger than the maximal length",
                                    edge));

                        // Checks whether the sink of the edge/edge view exist
                        HashSet<string> edgeSinkNodes =
                            _graphMetaData.ColumnsOfNodeTables[new Tuple<string, string>(schema, bindNode)][edge].EdgeInfo
                                .SinkNodes;
                        HashSet<string> sinkNodes;
                        if (
                            !edgeSinkNodes.All(
                                e =>
                                    _graphMetaData.ColumnsOfNodeTables.ContainsKey(new Tuple<string, string>(
                                        schema.ToLower(), e))))
                            throw new GraphViewException(String.Format(CultureInfo.CurrentCulture,
                                "Node Table Referenced by the Edge {0} not exists", edge));

                        // Checks validity of sink node(s)
                        var nextNode = index != count - 1
                            ? path.PathEdgeList[index + 1].Item1
                            : path.Tail;
                        var getNextTable = _context[nextNode.BaseIdentifier.Value];
                        if (!IsNodeTable(getNextTable))
                            throw new GraphViewException("Node table expected in MATCH clause");

                        // Checks whether the intersection of the edge sink and sink node(s) is empty
                        var nextTable = getNextTable as WNamedTableReference;
                        if (nextTable == null ||
                            !_graphMetaData.NodeViewMapping.TryGetValue(
                                WNamedTableReference.SchemaNameToTuple(nextTable.TableObjectName), out sinkNodes))
                            sinkNodes = new HashSet<string> {nextTable.TableObjectName.BaseIdentifier.Value};
                        if (sinkNodes.All(e => !edgeSinkNodes.Contains(e)))
                        {
                            throw new GraphViewException(String.Format(CultureInfo.CurrentCulture,
                                "Wrong Reference Table {0}", nextTable.TableObjectName.BaseIdentifier.Value));
                        }
                    }
                }
            }
            else
            {
                throw new GraphViewException("Node table/view expected in MATCH clause");
            }
        }

        /// <summary>
        /// If paths is referenced in the MATCH clause, checks the SELECT elements to decide whether the 
        /// paths informaion is needed to be displayed. If PathAlias.* occurs in the SELECT elements,
        /// sets the corresponding bool value in MatchPath, and replaces this element with 
        /// an scalar function to display readable path information
        /// </summary>
        /// <param name="query">Select query</param>
        /// <param name="pathDictionary">A collection of path alias and match path instance</param>
        private void TransformPathInfoDisplaySelectElement(WSelectQueryBlock query,
            Dictionary<string, MatchPath> pathDictionary)
        {

            if (pathDictionary!=null && pathDictionary.Any())
            {
                List<WSelectElement> newSelectElements = new List<WSelectElement>();
                foreach (var selectElement in query.SelectElements)
                {
                    var starElement = selectElement as WSelectStarExpression;
                    if (starElement != null && starElement.Qulifier != null)
                    {
                        var colName = starElement.Qulifier.Identifiers[starElement.Qulifier.Count - 1].Value;
                        MatchPath path;
                        if (pathDictionary.TryGetValue(colName, out path))
                        {
                            path.ReferencePathInfo = true;
                            string schema = path.BindNodeTableObjName.SchemaIdentifier.Value;
                            string tableName = path.BindNodeTableObjName.BaseIdentifier.Value;
                            string pathName = path.EdgeColumn.MultiPartIdentifier.Identifiers.Last().Value;
                            var parameters = new List<WScalarExpression>
                            {
                                new WColumnReferenceExpression
                                {
                                    MultiPartIdentifier =
                                        new WMultiPartIdentifier(new Identifier() {Value = path.EdgeAlias},
                                            new Identifier() {Value = "PathMessage"})
                                },
                            };
                            if (
                                _graphMetaData.NodeViewMapping.ContainsKey(
                                    WNamedTableReference.SchemaNameToTuple(path.SinkNode.NodeTableObjectName)))
                            {
                                parameters.Add(new WColumnReferenceExpression
                                {
                                    MultiPartIdentifier =
                                        new WMultiPartIdentifier(new Identifier() { Value = path.SinkNode.RefAlias },
                                            new Identifier() { Value = "_NodeType" })
                                });
                                parameters.Add(new WColumnReferenceExpression
                                {
                                    MultiPartIdentifier =
                                        new WMultiPartIdentifier(new Identifier() { Value = path.SinkNode.RefAlias },
                                            new Identifier() { Value = "_NodeId" })
                                });
                            }
                            else
                            {
                                parameters.Add(new WValueExpression
                                {
                                    Value = path.SinkNode.NodeTableObjectName.BaseIdentifier.Value,
                                    SingleQuoted = true
                                });
                                string sinkNodeIdName =
                                    _graphMetaData.ColumnsOfNodeTables[
                                        WNamedTableReference.SchemaNameToTuple(path.SinkNode.NodeTableObjectName)]
                                        .FirstOrDefault(e => e.Value.Role == WNodeTableColumnRole.NodeId).Key;
                                if (string.IsNullOrEmpty(sinkNodeIdName))
                                    parameters.Add(new WValueExpression { Value = "null" });
                                else
                                {
                                    parameters.Add(new WColumnReferenceExpression
                                    {
                                        MultiPartIdentifier =
                                            new WMultiPartIdentifier(new Identifier() { Value = path.SinkNode.RefAlias },
                                                new Identifier() { Value = sinkNodeIdName })
                                    });
                                }

                            }
                            newSelectElements.Add(new WSelectScalarExpression
                            {
                                ColumnName = path.EdgeAlias + "_PathInfo",
                                SelectExpr = new WFunctionCall
                                {
                                    CallTarget = new WMultiPartIdentifierCallTarget
                                    {
                                        Identifiers = new WMultiPartIdentifier(new Identifier { Value = "dbo" })
                                    },
                                    FunctionName = new Identifier { Value = string.Format("{0}_{1}_{2}_PathMessageDecoder", schema, tableName, pathName) },
                                    Parameters = parameters
                                }
                            });
                            continue;
                        }
                    }
                    newSelectElements.Add(selectElement);

                }
                query.SelectElements = newSelectElements;
            }
        }

        /// <summary>
        /// If a table alias in the MATCH clause is defined in an upper-level context,
        /// to be able to translate this MATCH clause, this table alias must be re-materialized
        /// in the FROM clause of the current context and joined with the corresponding table
        /// in the upper-level context.
        /// </summary>
        /// <param name="query">Select query</param>
        /// <param name="nodes">A collection of node alias and match node instance</param>
        private void RematerilizeExtrenalNodeTableReference(WSelectQueryBlock query, Dictionary<string, MatchNode> nodes)
        {
            var tableRefs = query.FromClause.TableReferences;
            var tableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newTableRefs = new List<WTableReference>();
            for (int index = 0; index < tableRefs.Count; ++index)
            {
                var table = tableRefs[index] as WNamedTableReference;
                if (table == null)
                {
                    newTableRefs.Add(tableRefs[index]);
                    continue;
                }
                if (!nodes.ContainsKey(table.ExposedName.Value))
                {
                    newTableRefs.Add(table);
                }
                else
                {
                    tableSet.Add(table.ExposedName.Value);
                }
            }
            query.FromClause = new WFromClause
            {
                TableReferences = newTableRefs,
            };
            WBooleanExpression whereCondiction = null;
            foreach (var node in nodes.Where(node => !tableSet.Contains(node.Key)))
            {
                node.Value.External = true;
                var newWhereCondition = new WBooleanComparisonExpression
                {
                    ComparisonType = BooleanComparisonType.Equals,
                    FirstExpr = new WColumnReferenceExpression
                    {
                        MultiPartIdentifier = new WMultiPartIdentifier(
                        new Identifier { Value = node.Key },
                        new Identifier { Value = "GlobalNodeId" })
                    },
                    SecondExpr = new WColumnReferenceExpression
                    {
                        MultiPartIdentifier = new WMultiPartIdentifier(
                        new Identifier { Value = node.Value.RefAlias },
                        new Identifier { Value = "GlobalNodeId" })
                    },
                };
                whereCondiction = WBooleanBinaryExpression.Conjunction(whereCondiction, newWhereCondition);
            }
            if (whereCondiction != null)
            {
                if (query.WhereClause == null)
                {
                    query.WhereClause = new WWhereClause { SearchCondition = whereCondiction };
                }
                else
                {
                    if (query.WhereClause.SearchCondition == null)
                    {
                        query.WhereClause.SearchCondition = whereCondiction;
                    }
                    else
                    {
                        query.WhereClause.SearchCondition = new WBooleanBinaryExpression
                        {
                            BooleanExpressionType = BooleanBinaryExpressionType.And,
                            FirstExpr = new WBooleanParenthesisExpression
                            {
                                Expression = query.WhereClause.SearchCondition
                            },
                            SecondExpr = new WBooleanParenthesisExpression
                            {
                                Expression = whereCondiction
                            }
                        };
                    }
                }
            }
        }


        /// <summary>
        /// Constructs the graph pattern specified by the MATCH clause. 
        /// The graph pattern may consist of multiple fully-connected sub-graphs.
        /// </summary>
        /// <param name="query">The SELECT query block</param>
        /// <returns>A graph object contains all the connected componeents</returns>
        private MatchGraph ConstructGraph(WSelectQueryBlock query)
        {
            if (query == null || query.MatchClause == null)
                return null;

            var unionFind = new UnionFind();
            var edgeColumnToAliasesDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pathDictionary = new Dictionary<string, MatchPath>(StringComparer.OrdinalIgnoreCase);
            var matchClause = query.MatchClause;
            var nodes = new Dictionary<string, MatchNode>(StringComparer.OrdinalIgnoreCase);
            var connectedSubGraphs = new List<ConnectedComponent>();
            var subGrpahMap = new Dictionary<string, ConnectedComponent>(StringComparer.OrdinalIgnoreCase);
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            unionFind.Parent = parent;

            // Constructs the graph pattern specified by the path expressions in the MATCH clause
            foreach (var path in matchClause.Paths)
            {
                var index = 0;
                MatchEdge preEdge = null;
                for (var count = path.PathEdgeList.Count; index < count; ++index)
                {
                    var currentNodeTableRef = path.PathEdgeList[index].Item1;
                    var currentEdgeColumnRef = path.PathEdgeList[index].Item2;
                    var currentNodeExposedName = currentNodeTableRef.BaseIdentifier.Value;
                    var nextNodeTableRef = index != count - 1
                        ? path.PathEdgeList[index + 1].Item1
                        : path.Tail;
                    var nextNodeExposedName = nextNodeTableRef.BaseIdentifier.Value;
                    var patternNode = nodes.GetOrCreate(currentNodeExposedName);
                    if (patternNode.NodeAlias == null)
                    {
                        patternNode.NodeAlias = currentNodeExposedName;
                        patternNode.Neighbors = new List<MatchEdge>();
                        patternNode.External = false;
                        var nodeTable = _context[currentNodeExposedName] as WNamedTableReference;
                        if (nodeTable != null)
                        {
                            patternNode.NodeTableObjectName = nodeTable.TableObjectName;
                            if (patternNode.NodeTableObjectName.SchemaIdentifier == null)
                                patternNode.NodeTableObjectName.Identifiers.Insert(0, new Identifier {Value = "dbo"});
                        }
                    }

                    string edgeAlias = currentEdgeColumnRef.Alias;
                    if (edgeAlias == null)
                    {
                        var currentEdgeName = currentEdgeColumnRef.MultiPartIdentifier.Identifiers.Last().Value;
                        edgeAlias = string.Format("{0}_{1}_{2}", currentNodeExposedName, currentEdgeName,
                            nextNodeExposedName);
                        if (edgeColumnToAliasesDict.ContainsKey(currentEdgeName))
                        {
                            edgeColumnToAliasesDict[currentEdgeName].Add(edgeAlias);
                        }
                        else
                        {
                            edgeColumnToAliasesDict.Add(currentEdgeName, new List<string> { edgeAlias });
                        }
                    }

                    Identifier edgeIdentifier = currentEdgeColumnRef.MultiPartIdentifier.Identifiers.Last();
                    string schema = patternNode.NodeTableObjectName.SchemaIdentifier.Value.ToLower();
                    string nodeTableName = patternNode.NodeTableObjectName.BaseIdentifier.Value;
                    string bindTableName =
                        _context.EdgeNodeBinding[
                            new Tuple<string, string>(nodeTableName.ToLower(), edgeIdentifier.Value.ToLower())].ToLower();
                    MatchEdge edge;
                    if (currentEdgeColumnRef.MinLength == 1 && currentEdgeColumnRef.MaxLength == 1)
                    {
                        edge = new MatchEdge
                        {
                            SourceNode = patternNode,
                            EdgeColumn = currentEdgeColumnRef,
                            EdgeAlias = edgeAlias,
                            BindNodeTableObjName =
                                new WSchemaObjectName(
                                    new Identifier {Value = schema},
                                    new Identifier {Value = bindTableName}
                                    ),
                        };
                        _context.AddEdgeReference(edge);
                    }
                    else
                    {
                        MatchPath matchPath = new MatchPath
                        {
                            SourceNode = patternNode,
                            EdgeColumn = currentEdgeColumnRef,
                            EdgeAlias = edgeAlias,
                            BindNodeTableObjName =
                                new WSchemaObjectName(
                                    new Identifier {Value = schema},
                                    new Identifier {Value = bindTableName}
                                    ),
                            MinLength = currentEdgeColumnRef.MinLength,
                            MaxLength = currentEdgeColumnRef.MaxLength,
                            ReferencePathInfo = false,
                            AttributeValueDict = currentEdgeColumnRef.AttributeValueDict
                        };
                        _context.AddEdgeReference(matchPath);
                        pathDictionary[edgeAlias] = matchPath;
                        edge = matchPath;
                    }

                    if (preEdge != null)
                    {
                        preEdge.SinkNode = patternNode;
                    }
                    preEdge = edge;

                    if (!parent.ContainsKey(currentNodeExposedName))
                        parent[currentNodeExposedName] = currentNodeExposedName;
                    if (!parent.ContainsKey(nextNodeExposedName))
                        parent[nextNodeExposedName] = nextNodeExposedName;

                    unionFind.Union(currentNodeExposedName, nextNodeExposedName);


                    patternNode.Neighbors.Add(edge);


                }
                var tailExposedName = path.Tail.BaseIdentifier.Value;
                var tailNode = nodes.GetOrCreate(tailExposedName);
                if (tailNode.NodeAlias == null)
                {
                    tailNode.NodeAlias = tailExposedName;
                    tailNode.Neighbors = new List<MatchEdge>();
                    var nodeTable = _context[tailExposedName] as WNamedTableReference;
                    if (nodeTable != null)
                    {
                        tailNode.NodeTableObjectName = nodeTable.TableObjectName;
                        if (tailNode.NodeTableObjectName.SchemaIdentifier == null)
                            tailNode.NodeTableObjectName.Identifiers.Insert(0, new Identifier {Value = "dbo"});
                    }
                }
                if (preEdge != null)
                    preEdge.SinkNode = tailNode;
            }

            // Puts nodes into subgraphs
            foreach (var node in nodes)
            {
                string root = unionFind.Find(node.Key);
                if (!subGrpahMap.ContainsKey(root))
                {
                    var subGraph = new ConnectedComponent();
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    subGrpahMap[root] = subGraph;
                    connectedSubGraphs.Add(subGraph);
                    subGraph.IsTailNode[node.Value] = false;
                }
                else
                {
                    var subGraph = subGrpahMap[root];
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    subGraph.IsTailNode[node.Value] = false;
                }
            }

            var graph = new MatchGraph
            {
                ConnectedSubGraphs = connectedSubGraphs,
            };
            unionFind.Parent = null;

            // When an edge in the MATCH clause is not associated with an alias, 
            // assigns to it a default alias: sourceAlias_EdgeColumnName_sinkAlias. 
            // Also rewrites edge attributes anywhere in the query that can be bound to this default alias. 
            var replaceTableRefVisitor = new ReplaceEdgeReferenceVisitor();
            replaceTableRefVisitor.Invoke(query, edgeColumnToAliasesDict);

            // Rematerializes node tables in the MATCH clause which are defined in the upper-level context
            // and join them with the upper-level table references.
            RematerilizeExtrenalNodeTableReference(query, nodes);

            // Transforms the path reference in the SELECT elements into a 
            // scalar function to display path information.
            TransformPathInfoDisplaySelectElement(query, pathDictionary);

            
            return graph;
        }

        /// <summary>
        /// Replaces the SELECT * expression with all visible columns
        /// </summary>
        /// <param name="node"></param>
        /// <param name="graph"></param>
        private void ChangeSelectStarExpression(WSelectQueryBlock node, MatchGraph graph)
        {
            var newSelectElements = new List<WSelectElement>();
            Dictionary<string, List<WSelectElement>> starReplacement = null;
            foreach (var element in node.SelectElements)
            {
                var starElement = element as WSelectStarExpression;
                if (starElement != null)
                {
                    if (starReplacement == null)
                    {
                        starReplacement =
                            new Dictionary<string, List<WSelectElement>>(StringComparer.OrdinalIgnoreCase);
                        // Fetch table in order
                        foreach (var table in _context.NodeTableDictionary)
                        {
                            var alias = table.Key;
                            var namedTable = table.Value as WNamedTableReference;
                            if (namedTable != null)
                            {
                                foreach (
                                    var column in
                                        _graphMetaData.ColumnsOfNodeTables[
                                            WNamedTableReference.SchemaNameToTuple(namedTable.TableObjectName)].Where(
                                                e => e.Value.Role != WNodeTableColumnRole.Edge).Select(e => e.Key))
                                {
                                    var elementList = starReplacement.GetOrCreate(alias);
                                    elementList.Add(new WSelectScalarExpression
                                    {
                                        SelectExpr = new WColumnReferenceExpression
                                        {
                                            MultiPartIdentifier = new WMultiPartIdentifier
                                            {
                                                Identifiers = new List<Identifier>
                                                {
                                                    new Identifier {Value = alias},
                                                    new Identifier {Value = column}
                                                }
                                            }
                                        }

                                    });
                                }
                                if (graph == null) continue;
                                foreach (var subGraph in graph.ConnectedSubGraphs)
                                {
                                    if (subGraph.Nodes.ContainsKey(alias))
                                    {
                                        var matchNode = subGraph.Nodes[alias];
                                        foreach (var edge in matchNode.Neighbors)
                                        {
                                            var schemaName = edge.SourceNode.NodeTableObjectName.SchemaIdentifier ==
                                                             null
                                                ? "dbo"
                                                : edge.SourceNode.NodeTableObjectName.SchemaIdentifier.Value.ToLower();
                                            var nodeTuple = new Tuple<string, string>(schemaName,
                                                edge.SourceNode.NodeTableObjectName.BaseIdentifier.Value.ToLower());
                                            var edgeColumnName =
                                                edge.EdgeColumn.MultiPartIdentifier.Identifiers.Last().Value.ToLower();
                                            if (!_graphMetaData.ColumnsOfNodeTables[nodeTuple].ContainsKey(edgeColumnName))
                                            {
                                                throw new GraphViewException("Invalid Edge Alias");
                                            }
                                            foreach (
                                                var column in
                                                    _graphMetaData.ColumnsOfNodeTables[nodeTuple][edgeColumnName].EdgeInfo
                                                        .ColumnAttributes)
                                            {
                                                var elementList = starReplacement.GetOrCreate(edge.EdgeAlias);
                                                elementList.Add(new WSelectScalarExpression
                                                {
                                                    SelectExpr = new WColumnReferenceExpression
                                                    {
                                                        MultiPartIdentifier = new WMultiPartIdentifier
                                                        {
                                                            Identifiers = new List<Identifier>
                                                            {
                                                                new Identifier {Value = edge.EdgeAlias},
                                                                new Identifier {Value = column}
                                                            }
                                                        }
                                                    }

                                                });
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var derivedTable = table.Value as WQueryDerivedTable;
                                if (derivedTable == null)
                                    continue;
                                var elementList = starReplacement.GetOrCreate(alias);
                                elementList.Add(new WSelectStarExpression
                                {
                                    Qulifier = new WMultiPartIdentifier
                                    {
                                        Identifiers = new List<Identifier> {new Identifier {Value = alias}}
                                    }
                                });

                            }
                        }
                    }
                    if (starElement.Qulifier != null)
                    {
                        newSelectElements.AddRange(starReplacement[starElement.Qulifier.Identifiers.Last().Value]);
                    }
                    else
                    {
                        foreach (var value in starReplacement.Values)
                        {
                            newSelectElements.AddRange(value);
                        }
                    }
                }
                else
                {
                    newSelectElements.Add(element);
                }
            }
            if (newSelectElements.Any())
                node.SelectElements = newSelectElements;
        }

        /// <summary>
        /// Remove the unnecessary tail node table
        /// </summary>
        /// <param name="query"></param>
        /// <param name="graph"></param>
        private void OptimizeTail(WSelectQueryBlock query, MatchGraph graph)
        {
            var visitor = new CheckTableReferenceVisitor();
            foreach (var connectedSubGraph in graph.ConnectedSubGraphs)
            {
                var toRemove = connectedSubGraph.Nodes.Where(
                    node => node.Value.Neighbors.Count == 0 &&
                            !visitor.Invoke(query, node.Key, _context, _graphMetaData.ColumnsOfNodeTables)
                    )
                    .ToArray();
                foreach (var item in toRemove)
                {
                    connectedSubGraph.IsTailNode[item.Value] = true;
                }
            }
        }

        private void AttachPredicates(WWhereClause whereClause, MatchGraph graph)
        {
            // Attaches proper parts of the where clause into the Estimiation Query
            var attachPredicateVisitor = new AttachWhereClauseVisitor();
            var columnTableMapping = _context.GetColumnToAliasMapping(_graphMetaData.ColumnsOfNodeTables);
            attachPredicateVisitor.Invoke(whereClause, graph, columnTableMapping);
        }
        
        /// <summary>
        /// Estimates number of rows of node table in graph.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="graph">Constructed node graph</param>
        private void EstimateRows(WSelectQueryBlock query, MatchGraph graph)
        {
            var declareParameter = "";
            if (_variables != null)
            {
                declareParameter = _variables.Aggregate(declareParameter,
                    (current, parameter) =>
                        current +
                        ("DECLARE " + parameter.VariableName.Value + " " +
                         TsqlFragmentToString.DataType(parameter.DataType) + "\r\n"));
            }

            // Attaches predicates to nodes and edges
            var estimator = new TableSizeEstimator(Tx);
            bool first = true;
            var selectQuerySb = new StringBuilder(1024);
            foreach (var subGraph in graph.ConnectedSubGraphs)
            {
                foreach (var node in subGraph.Nodes)
                {
                    if (first)
                        first = false;
                    else
                        selectQuerySb.Append("\r\nUNION ALL\r\n");
                    var currentNode = node.Value;
                    selectQuerySb.AppendFormat("SELECT GlobalNodeId FROM {0} AS [{1}] WITH (ForceScan)",
                        currentNode.NodeTableObjectName,
                        currentNode.NodeAlias);
                    if (currentNode.Predicates != null && currentNode.Predicates.Count > 0)
                    {
                        selectQuerySb.AppendFormat("\r\nWHERE {0}", currentNode.Predicates[0]);
                        for (int i = 1; i < currentNode.Predicates.Count; i++)
                        {
                            var predicate = currentNode.Predicates[i];
                            selectQuerySb.AppendFormat(" AND {0}", predicate);
                        }
                    }
                }
            }

            // TODO: Can be distinguished between nodeview and node. E.g.:
            //             SELECT count(*) FROM [dbo].[EmployeeNode] AS [e1] WITH (ForceScan)
            //                                  ,[dbo].[EmployeeNode] AS [e2] WITH (ForceScan)
            //                                  , [dbo].[ClientNode] AS [c1] WITH (ForceScan)
            //                                  ,[dbo].[ClientNode] AS [c2] WITH (ForceScan)
            //             WHERE e1.WorkId!=e2.WorkId
            //             UNION ALL
            //             SELECT COUNT(*) FROM [dbo].[NV1] AS [NV1] WITH (ForceScan)
            //              WHERE [NV1].[id] = 10

            string nodeEstimationQuery = string.Format("{0}\r\n {1}\r\n", declareParameter,
                selectQuerySb);
            var estimateRows = estimator.GetUnionQueryTableEstimatedRows(nodeEstimationQuery);

            int j = 0;
            foreach (var subGraph in graph.ConnectedSubGraphs)
            {
                // Update Row Estimation for nodes
                foreach (var node in subGraph.Nodes)
                {
                    var currentNode = node.Value;
                    var tableSchema = currentNode.NodeTableObjectName.SchemaIdentifier.Value;
                    var tableName = currentNode.NodeTableObjectName.BaseIdentifier.Value;
                    var tableTuple = WNamedTableReference.SchemaNameToTuple(currentNode.NodeTableObjectName);
                    if (_graphMetaData.NodeViewMapping.ContainsKey(tableTuple))
                    {
                        var nodeSet = _graphMetaData.NodeViewMapping[tableTuple];
                        int n = nodeSet.Count;
                        double nodeViewEstRows = 0.0;
                        while (n > 0)
                        {
                            n--;
                            nodeViewEstRows += estimateRows[j];
                            j++;
                        }
                        currentNode.EstimatedRows = nodeViewEstRows;
                        currentNode.TableRowCount = nodeSet.Aggregate(0,
                            (cur, next) => cur + estimator.GetTableRowCount(tableSchema, next));
                    }
                    else
                    {
                        currentNode.EstimatedRows = estimateRows[j];
                        currentNode.TableRowCount = estimator.GetTableRowCount(tableSchema, tableName);
                        j++;
                    }
                }
            }
        }

        /// <summary>
        /// Estimates the average degree of the edges and retrieve density value.
        /// Send sa query to retrieve the varbinary of the sink in the edge sampling table with edge predicates,
        /// then generates the statistics histogram for each edge
        /// </summary>
        private void RetrieveStatistics(MatchGraph graph)
        {
            if (graph == null) throw new ArgumentNullException("graph");
            // Declare the parameters if any
            var declareParameter = "";
            if (_variables != null)
            {
                declareParameter = _variables.Aggregate(declareParameter,
                    (current, parameter) =>
                        current +
                        ("DECLARE " + parameter.VariableName.Value + " " +
                         TsqlFragmentToString.DataType(parameter.DataType) + "\r\n"));
            }

            // Calculates the average degree
            var sb = new StringBuilder();
            bool first = true;
            sb.Append("SELECT [Edge].*, [EdgeDegrees].[SampleRowCount], [EdgeDegrees].[AverageDegree] FROM");
            sb.Append("(\n");
            foreach (var edge in graph.ConnectedSubGraphs.SelectMany(subGraph => subGraph.Edges.Values))
            {
                if (!first)
                    sb.Append("\nUNION ALL\n");
                else
                {
                    first = false;
                }
                var tableObjectName = edge.SourceNode.NodeTableObjectName;
                string schema = tableObjectName.SchemaIdentifier.Value.ToLower();
                string tableName = tableObjectName.BaseIdentifier.Value.ToLower();
                string edgeName = edge.EdgeColumn.MultiPartIdentifier.Identifiers.Last().Value.ToLower();
                string bindTableName = _context.EdgeNodeBinding[new Tuple<string, string>(tableName, edgeName)];

                // Distinguished between path and edge
                //var sinkColumnName = edge.IsPath ? "COUNT(Sink)" : "[dbo].[GraphViewUDFGlobalNodeIdEncoder](Sink)";
                sb.Append(
                    string.Format(@"
                            SELECT '{0}' as TableSchema, 
                                   '{1}' as TableName, 
                                   '{2}' as ColumnName,
                                   '{3}' as Alias, 
                                    [dbo].[GraphViewUDFGlobalNodeIdEncoder](Sink) as Sink
                            FROM [{0}_{1}_{2}_Sampling] as [{3}]", schema,
                        bindTableName,
                        edgeName,
                        edge.EdgeAlias));
                var predicatesExpr = edge.RetrievePredicatesExpression();
                if (predicatesExpr!=null)
                    sb.AppendFormat("\n WHERE {0}", predicatesExpr);
                    }
            sb.Append("\n) as Edge \n");
            sb.Append(String.Format(@"
                        INNER JOIN
                            [{0}] as [EdgeDegrees]
                        ON 
                            [EdgeDegrees].[TableSchema] = [Edge].[TableSchema] 
                        AND [EdgeDegrees].[TableName] = [Edge].[TableName] 
                        AND [EdgeDegrees].[ColumnName] = [Edge].[ColumnName]", GraphViewConnection.MetadataTables[3]));

            using (var command = Tx.Connection.CreateCommand())
            {
                command.Transaction = Tx;
                command.CommandText = declareParameter + sb.ToString();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        MatchEdge edge;
                        if (!graph.TryGetEdge(reader["Alias"].ToString(), out edge))
                            throw new GraphViewException(string.Format("Edge {0} not exists", reader["Alias"].ToString()));
                        var sinkBytes = reader["Sink"] as byte[];
                        if (sinkBytes == null)
                        {
                            edge.Statistics = new EdgeStatistics
                            {
                                Density = 0,
                                Histogram = new Dictionary<long, Tuple<double, bool>>(),
                                MaxValue = 0,
                                RowCount = 0,
                                Selectivity = 1.0
                            };
                            continue;
                        }
                        List<long> sinkList = new List<long>();
                        var cursor = 0;
                        while (cursor < sinkBytes.Length)
                        {
                            var sink = BitConverter.ToInt64(sinkBytes, cursor);
                            cursor += 8;
                            sinkList.Add(sink);
                        }
                        EdgeStatistics.UpdateEdgeHistogram(edge, sinkList);
                        edge.AverageDegree = Convert.ToDouble(reader["AverageDegree"])*sinkList.Count*1.0/
                                             Convert.ToInt64(reader["SampleRowCount"]);
                        var path = edge as MatchPath;
                        if (path != null)
                        {
                            if (path.AverageDegree > 1)
                                if (path.MaxLength != -1)
                                {
                                    path.AverageDegree = Math.Pow(path.AverageDegree, path.MaxLength) -
                                                         (path.MinLength > 0
                                                             ? Math.Pow(path.AverageDegree, path.MinLength - 1)
                                                             : 0);
                                }
                                else
                                    path.AverageDegree = double.MaxValue;
                        }

                    }
                }

                // Retrieves density value for each node table
                string tempTableName = Path.GetRandomFileName().Replace(".", "").Substring(0, 8);
                var dbccDensityQuery = new StringBuilder();
                dbccDensityQuery.Append(string.Format(@"CREATE TABLE #{0} (Density float, Len int, Col sql_variant);
                                                    INSERT INTO #{0} EXEC('", tempTableName));
                Dictionary<Tuple<string, string>, List<MatchNode>> schemaTableToNodeListMapping =
                    new Dictionary<Tuple<string, string>, List<MatchNode>>();
                foreach (var subGraph in graph.ConnectedSubGraphs)
                {
                    foreach (var node in subGraph.Nodes.Values)
                    {
                        var tableTuple = WNamedTableReference.SchemaNameToTuple(node.NodeTableObjectName);
                        if (_graphMetaData.NodeViewMapping.ContainsKey(tableTuple))
                        {
                            node.GlobalNodeIdDensity = EdgeStatistics.DefaultDensity;
                        }
                        else
                        {
                            var nodeList = schemaTableToNodeListMapping.GetOrCreate(tableTuple);
                            nodeList.Add(node);
                        }
                        
                    }
                }
                foreach (var tableTuple in schemaTableToNodeListMapping.Keys)
                {
                    dbccDensityQuery.Append(string.Format(
                        "DBCC SHOW_STATISTICS (\"{0}.{1}\", [{0}{1}_PK_GlobalNodeId]) with DENSITY_VECTOR;\n",
                        tableTuple.Item1,
                        tableTuple.Item2));
                }
                dbccDensityQuery.Append("');\n");
                dbccDensityQuery.Append(string.Format("SELECT Density FROM #{0} WHERE Col = 'GlobalNodeId'", tempTableName));
                command.CommandText = dbccDensityQuery.ToString();
                using (var reader = command.ExecuteReader())
                {
                    foreach (var item in schemaTableToNodeListMapping)
                    {
                        double density;
                        if (!reader.Read())
                            density = EdgeStatistics.DefaultDensity;
                        else
                        {
                            density = Convert.ToDouble(reader["Density"]);
                            if (Math.Abs(density - 1.0) < 0.0001)
                                density = EdgeStatistics.DefaultDensity;
                        }
                        
                        foreach (var node in item.Value)
                        {
                            node.GlobalNodeIdDensity = density;
                        }
                    }
                }
            }
        }

        // TODO: Use Heap O(logN)
        /// <summary>
        /// Gets the index and the average size per edege of the component with maximum average size
        /// </summary>
        /// <param name="components"></param>
        /// <returns></returns>
        private Tuple<int, double> GetMostExpensiveMatchComponent(List<MatchComponent> components)
        {
            int index = 0;
            int edgeCount = components[0].EdgeMaterilizedDict.Count;
            edgeCount = edgeCount == 0 ? 1 : edgeCount;
            double maxValue = components[0].Cost/edgeCount;
            for (int i = 1; i < components.Count; i++)
            {
                edgeCount = components[i].EdgeMaterilizedDict.Count;
                edgeCount = edgeCount == 0 ? 1 : edgeCount;
                if (components[i].Cost/edgeCount > maxValue)
                {
                    index = i;
                    maxValue = components[i].Cost/edgeCount;
                }
            }
            return new Tuple<int, double>(index, maxValue);
        }

        /// <summary>
        /// Get a full one height tree with joint edges and unmaterlized edges,
        /// returns a tuple whose first item is the one height tree and the second item
        /// indicates whether the one height tree only joins with the component's materialized
        /// node on its root.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="component"></param>
        /// <returns></returns>
        private IEnumerable<Tuple<OneHeightTree, bool>> GetNodeUnits(ConnectedComponent graph, MatchComponent component)
        {
            var nodes = graph.Nodes;
            foreach (var node in nodes.Values.Where(e => !graph.IsTailNode[e]))
            {
                bool joint = false;
                var jointEdges = new List<MatchEdge>();
                var nodeEdgeDict = node.Neighbors.ToDictionary(e => e,
                    e => component.EdgeMaterilizedDict.ContainsKey(e));

                // Edge to component node
                foreach (var edge in node.Neighbors.Where(e => !nodeEdgeDict[e]))
                {
                    if (component.Nodes.Contains(edge.SinkNode))
                    {
                        joint = true;
                        nodeEdgeDict[edge] = true;
                        jointEdges.Add(edge);
                    }
                }

                // Component edge to node
                if (!joint && component.UnmaterializedNodeMapping.ContainsKey(node))
                {
                    joint = true;
                }

                // Add unpopulated edges
                var nodeUnpopulatedEdges = nodeEdgeDict.Where(e => !e.Value).Select(e => e.Key).ToList();
                if (joint)
                    yield return new Tuple<OneHeightTree, bool>(new OneHeightTree
                    {
                        TreeRoot = node,
                        MaterializedEdges = jointEdges,
                        UnmaterializedEdges = nodeUnpopulatedEdges,
                    }, false);

                // Single node edge
                else if (nodeUnpopulatedEdges.Count > 0 && component.MaterializedNodeSplitCount.Count > 1 &&
                         component.MaterializedNodeSplitCount.ContainsKey(node))
                {
                    yield return new Tuple<OneHeightTree, bool>(new OneHeightTree
                    {
                        TreeRoot = node,
                        MaterializedEdges = jointEdges,
                        UnmaterializedEdges = nodeUnpopulatedEdges,
                    }, true);
                }
            }
        }



        /// <summary>
        /// Get the optimal join component for the given connected graph
        /// 1. Generate the initial states
        /// 2. DP, Iterate on each states: 
        ///     Get smallest join units -> Enumerate on all possible combination of the join units
        ///     -> Join to the current component to get the next states 
        ///     -> Those components with the largest average size per edge will be eliminate if exceeding the upper bound
        /// 3. If all the components has reached its end states, return the component with the smallest join cost
        /// </summary>
        /// <param name="subGraph"></param>
        /// <returns></returns>
        private MatchComponent ConstructComponent(ConnectedComponent subGraph)
        {
            var componentStates = new List<MatchComponent>();
            var nodes = subGraph.Nodes;
            MatchComponent finishedComponent = null;

            //Init
            double maxValue = Double.MinValue;
            foreach (var node in nodes)
            {
                if (!subGraph.IsTailNode[node.Value])
                {
                    // Enumerate on each edge for a node to generate the intial states
                    var edgeCount = node.Value.Neighbors.Count;
                    int eNum = (int) Math.Pow(2, edgeCount) - 1;
                    while (eNum > 0)
                    {
                        var nodeInitialEdges = new List<MatchEdge>();
                        for (int i = 0; i < edgeCount; i++)
                        {
                            int index = (1 << i);
                            if ((eNum & index) != 0)
                            {
                                nodeInitialEdges.Add(node.Value.Neighbors[i]);
                            }
                        }
                        componentStates.Add(new MatchComponent(node.Value, nodeInitialEdges, _context, _graphMetaData));
                        eNum--;
                    }
                }
            }


            // DP
            while (componentStates.Any())
            {
                int maxIndex = -1;
                var nextCompnentStates = new List<MatchComponent>();

                // Iterate on current components
                foreach (var curComponent in componentStates)
                {
                    var nodeUnits = GetNodeUnits(subGraph, curComponent);
                    if (!nodeUnits.Any())
                    {
                        if (finishedComponent == null || curComponent.Cost < finishedComponent.Cost)
                        {
                            finishedComponent = curComponent;
                        }
                        continue;
                    }

                    var candidateUnits = _pruningStrategy.GetCandidateUnits(nodeUnits, curComponent);

                    // Iterates on the candidate node units & add it to the current component to generate next states
                    foreach (var candidateUnit in candidateUnits)
                    {
                        // Pre-filter. If the lower bound of the current totoal join cost
                        // > current optimal join cost, prunes this component.
                        if (finishedComponent != null)
                        {
                            double candidateSize = candidateUnit.TreeRoot.EstimatedRows*
                                                   candidateUnit.UnmaterializedEdges.Select(e => e.AverageDegree)
                                                       .Aggregate(1.0, (cur, next) => cur*next)*
                                                   candidateUnit.MaterializedEdges.Select(e => e.AverageDegree)
                                                       .Aggregate(1.0, (cur, next) => cur*next);
                            double costLowerBound = curComponent.Size + candidateSize;
                            if (candidateUnit.MaterializedEdges.Count == 0)
                                costLowerBound = Math.Min(costLowerBound,
                                    Math.Log(candidateUnit.TreeRoot.EstimatedRows, 512));
                            if (curComponent.Cost + costLowerBound >
                                finishedComponent.Cost )
                            {
                                continue;
                            }
                        }

                        var newComponent = curComponent.GetNextState(candidateUnit, _statisticsCalculator);
                        if (nextCompnentStates.Count >= MaxStates)
                        {
                            if (maxIndex < 0)
                            {
                                var tuple = GetMostExpensiveMatchComponent(nextCompnentStates);
                                maxIndex = tuple.Item1;
                                maxValue = tuple.Item2;
                            }
                            else
                            {
                                int edgeCount = newComponent.EdgeMaterilizedDict.Count;
                                edgeCount = edgeCount == 0 ? 1 : edgeCount;
                                if (newComponent.Cost/edgeCount < maxValue)
                                {
                                    nextCompnentStates[maxIndex] = newComponent;
                                    var tuple = GetMostExpensiveMatchComponent(nextCompnentStates);
                                    maxIndex = tuple.Item1;
                                    maxValue = tuple.Item2;
                                }
                                continue;
                            }
                        }
                        nextCompnentStates.Add(newComponent);
                    }
                }
                componentStates = nextCompnentStates;
            }

            return finishedComponent;

        }

        /// <summary>
        /// Update FROM clause, adds DOWNSIZE predicates in the corresponding join conditions,
        /// and add corresponding predicates on the spilt nodes in the WHERE clause using 
        /// optimal component of each connected sub-graph.
        /// </summary>
        /// <param name="node">The SELECT statement</param>
        /// <param name="components">The optimal components of each fully-connected graph</param>
        private void UpdateQuery(WSelectQueryBlock node, List<MatchComponent> components)
        {
            // Removes schema name in SELECT clause and all column references.
            var removeSchemaVisitor = new RemoveSchemanameInIdentifersVisitor();
            removeSchemaVisitor.Visit(node);

            string newWhereString = "";
            foreach (var component in components)
            {
                // Adds down size predicates
                foreach (var joinTableTuple in component.FatherListofDownSizeTable)
                {
                    var joinTable = joinTableTuple.Item1;
                    var downSizeFunctionCall = new WFunctionCall
                    {
                        CallTarget = new WMultiPartIdentifierCallTarget
                        {
                            Identifiers = new WMultiPartIdentifier(new Identifier {Value = "dbo"})
                        },
                        FunctionName = new Identifier {Value = "DownSizeFunction"},
                        Parameters = new List<WScalarExpression>
                        {
                            new WColumnReferenceExpression
                            {
                                MultiPartIdentifier = new WMultiPartIdentifier
                                {
                                    Identifiers = new List<Identifier>
                                    {
                                        new Identifier {Value = joinTableTuple.Item2},
                                        new Identifier {Value = "LocalNodeid"}
                                    }
                                }
                            }
                        }
                    };
                    joinTable.JoinCondition = WBooleanBinaryExpression.Conjunction(joinTable.JoinCondition,
                        new WBooleanParenthesisExpression
                        {
                            Expression = new WBooleanBinaryExpression
                            {
                                BooleanExpressionType = BooleanBinaryExpressionType.Or,
                                FirstExpr = new WBooleanComparisonExpression
                                {
                                    ComparisonType = BooleanComparisonType.Equals,
                                    FirstExpr = downSizeFunctionCall,
                                    SecondExpr = new WValueExpression("1", false)
                                },
                                SecondExpr = new WBooleanComparisonExpression
                                {
                                    ComparisonType = BooleanComparisonType.Equals,
                                    FirstExpr = downSizeFunctionCall,
                                    SecondExpr = new WValueExpression("2", false)
                                }
                            }
                        });
                }

                // Updates from clause
                node.FromClause.TableReferences.Add(component.TableRef);

                // Adds predicates for split nodes
                var component1 = component;
                foreach (
                    var compNode in
                        component.MaterializedNodeSplitCount.Where(
                            e => e.Value > 0 && e.Key.Predicates != null && e.Key.Predicates.Any()))
                {
                    var matchNode = compNode.Key;

                    WBooleanExpression newExpression =
                        matchNode.Predicates.Aggregate<WBooleanExpression, WBooleanExpression>(null,
                            WBooleanBinaryExpression.Conjunction);
                    string predicateString = newExpression.ToString();
                    var nodeCount = component1.MaterializedNodeSplitCount[matchNode];

                    while (nodeCount > 0)
                    {
                        newWhereString += " AND ";
                        string tempStr = predicateString.Replace(string.Format("[{0}]", matchNode.RefAlias.ToUpper()),
                            string.Format("[{0}_{1}]", matchNode.RefAlias.ToUpper(), nodeCount));
                        tempStr = tempStr.Replace(string.Format("[{0}]", matchNode.RefAlias.ToLower()),
                            string.Format("[{0}_{1}]", matchNode.RefAlias.ToLower(), nodeCount));
                        newWhereString += tempStr;
                        nodeCount--;
                    }
                }
            }
            if (newWhereString.Any())
            {
                node.WhereClause.SearchCondition = new WBooleanParenthesisExpression
                {
                    Expression = node.WhereClause.SearchCondition
                };
                node.WhereClause.GhostString = newWhereString;
            }
            
        }

        /// <summary>
        /// Records the declared parameter in the Store Procedure Statement
        /// </summary>
        /// <param name="node"></param>
        public override void Visit(WProcedureStatement node)
        {
            var upperPar = _variables;
            if (node.Parameters != null)
            {
                if (_variables == null)
                {
                    _variables = new List<DeclareVariableElement>();
                }
                else
                {
                    _variables = new List<DeclareVariableElement>(_variables);
                }
                foreach (var parameter in node.Parameters)
                {
                    _variables.Add(parameter);
                }
            }
            base.Visit(node);
            _variables = upperPar;
        }

        public override void Visit(WDeclareVariableStatement node)
        {
            if (node.Statement.Declarations != null)
            {
                if (_variables == null)
                {
                    _variables = new List<DeclareVariableElement>();
                }
                foreach (var parameter in node.Statement.Declarations)
                {
                    _variables.Add(parameter);
                }
            }
        }

        /// <summary>
        /// The entry point of the optimizer, activated when visting each SELECT query block.
        /// </summary>
        /// <param name="node">The SELECT query block</param>
        public override void Visit(WSelectQueryBlock node)
        {
            var checkVarVisitor = new CollectVariableVisitor();
            var currentContext = checkVarVisitor.Invoke(node.FromClause, _graphMetaData.ColumnsOfNodeTables.Keys);
            currentContext.ParentContext = _context;
            _context = currentContext;

            base.Visit(node);

            CheckValidity(node);
            var graph = ConstructGraph(node);
            //ChangeSelectStarExpression(node, graph);

            if (graph != null)
            {
                OptimizeTail(node, graph);
                AttachPredicates(node.WhereClause,graph);
                EstimateRows(node, graph);
                RetrieveStatistics(graph);

                var components = new List<MatchComponent>();
                foreach (var subGraph in graph.ConnectedSubGraphs)
                {


                    components.Add(ConstructComponent(subGraph));
#if DEBUG
                    foreach (var matchNode in subGraph.Nodes.Values)
                    {
                        Trace.WriteLine(matchNode.NodeAlias);
                        Trace.WriteLine(string.Format("  RowCount:{0}", matchNode.TableRowCount));
                        Trace.WriteLine(string.Format("  EstiRow:{0}", matchNode.EstimatedRows));
                    }
#endif

                }

                UpdateQuery(node, components);

#if DEBUG
                Trace.WriteLine(string.Format("Rows:{0}", components[0].Size));
                Trace.WriteLine(string.Format("Cost:{0}", components[0].Cost));
                Trace.WriteLine(string.Format("Estimated Rows:{0}", components[0].EstimateSize));

#endif
                node.MatchClause = null;
            }

            _context = _context.ParentContext;
        }

    }
}
