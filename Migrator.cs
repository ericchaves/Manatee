using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Data.Common;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Text;
using System.Reflection;

namespace Manatee {
    public class Migrator {
        
        private string _datatypes;
        private string _migrationPath;
        private int _currentVersion;
        private Database _db;
        bool _showOutput = true;
        private List<string> _invalid;
        public SortedList<string, dynamic> Migrations { get; private set; }
        
        void Execute(string query, params Database[] dbs) {
            foreach (var db in dbs) {
                db.Execute(query);
            }
        }
        
        internal class Database {
            private DbProviderFactory _factory;
            private string _connectionString;

            public Database(string connectionStringName) {
                SetupConnectionAndFactory(connectionStringName);
            }

            public object QueryValue(string query) {
                using (var con = OpenConnection()) {
                    var command = CreateCommand(con, query);
                    return command.ExecuteScalar();
                }
            }

            public void Execute(string query) {
                using (var con = OpenConnection()) {
                    var command = CreateCommand(con, query);
                    command.ExecuteNonQuery();
                }
            }

            private DbCommand CreateCommand(DbConnection connection, string sql) {
                var command = _factory.CreateCommand();
                command.Connection = connection;
                command.CommandText = sql;
                return command;
            }

            private DbConnection OpenConnection() {
                var connection = _factory.CreateConnection();
                connection.ConnectionString = _connectionString;
                connection.Open();
                return connection;
            }

            private void SetupConnectionAndFactory(string connectionStringName) {
                if (connectionStringName == "") {
                    connectionStringName = ConfigurationManager.ConnectionStrings[0].Name;
                }

                var providerName = "System.Data.SqlClient";
                if (ConfigurationManager.ConnectionStrings[connectionStringName] != null) {
                    if (!string.IsNullOrEmpty(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName)) {
                        providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
                    }
                } else {
                    throw new InvalidOperationException("Can't find a connection string with the name '" + connectionStringName + "'");
                }

                _factory = DbProviderFactories.GetFactory(providerName);
                _connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
            }
        }

        public Migrator(string pathToDatatypes, string pathToMigrationFiles, string connectionStringName = "", bool silent = false) {
            _invalid = new List<string>();
            _datatypes = pathToDatatypes;
            _migrationPath = pathToMigrationFiles;
            _db = new Database(connectionStringName);
            Migrations = LoadMigrations(_migrationPath);
            EnsureSchema(_db);
            _showOutput = !silent;
            _currentVersion = Migrations.IndexOfKey((string)_db.QueryValue("SELECT Version from SchemaInfo"));
        }

        public void Reload()
        {
            Migrations = LoadMigrations(_migrationPath);
            EnsureSchema(_db);
            _currentVersion = Migrations.IndexOfKey((string)_db.QueryValue("SELECT Version from SchemaInfo"));
        }
        
        public int LastVersion {
            get {
                return Migrations.Count;
            }
        }

        public int CurrentVersion {
            get { return _currentVersion + 1; }
        }

        public string CurrentMigration
        {
            get { return _currentVersion == -1 ? "" : Migrations.Keys.ElementAt(_currentVersion); }
        }

        public bool HasErrors
        {
            get { return _invalid.Count > 0;  }
        }

        public string[] InvalidMigrations
        {
            get { return _invalid.ToArray();  }
        }
 
        public void ListMigrations(string label)
        {
            // Force outoput to show
            bool showOutput = _showOutput;
            _currentVersion = Migrations.IndexOfKey((string)_db.QueryValue("SELECT Version from SchemaInfo"));
            _showOutput = true;
            Log(string.Format("Migrations list in {0} enviroment.\r\n", label));

            for (int i = 0; i < LastVersion; i++)
                Log("{0} {1}{2}{3}", i + 1, (i > _currentVersion ? "+" : "-"), Migrations.Keys.ElementAt(i), 
                    _invalid.IndexOf(Migrations.Keys.ElementAt(i)) == -1 ? "" : " !!");
            Log("DB Current version: {0}", CurrentVersion);
            _showOutput = showOutput;
        }

        void Log(string message, params object[] formatArgs)
        {
            if (_showOutput) {
                Console.WriteLine(message,formatArgs);
            }
        }

        public void Migrate(int to = -1, bool execute = true)
        {
            if (HasErrors)
            {
                Log("{0} migrations malformed. Please fix it before running them.", _invalid.Count);
                return;
            }

            // Check heads and tails
            if ((to > Migrations.Count) || (CurrentVersion < 0))
            {
                Log("No version {0} found. Please check if your migrations are ok.", to);
                return;
            }
            if (execute == false)
                Log("******** PRINT ONLY. NO COMMANDS ARE BEING SENT. ********");
            
            int current = _currentVersion; // Save position for later
            if (CurrentVersion < to)
            {
                #region GOING UP     
                Log("Migrating from {0} to {1}", CurrentVersion, to);
                while (CurrentVersion < to)
                {
                    string migration_name = Migrations.Keys.ElementAt(++_currentVersion);
                    var migration = Migrations[migration_name];
                    Log("++ VERSION {0} Command: {1}", CurrentVersion, migration_name);
                    string sql = GetCommand(migration.up);
                    Log(sql);
                    if (execute)
                    {
                        _db.Execute(sql);
                        _db.Execute(string.Format("UPDATE SchemaInfo SET Version = '{0}'", migration_name));
                    }
                    Log("----------------------------------------------------\r\n");
                }
                #endregion
            }
            else if (CurrentVersion > to)
            {
                #region Going Down
                Log("Migrating down from {0} to {1}", CurrentVersion, to);
                while (CurrentVersion > to)
                {
                    string migration_name = Migrations.Keys.ElementAt(_currentVersion);
                    var migration = Migrations[migration_name];
                    Log("++ VERSION {0} Command: {1}", CurrentVersion, migration_name);
                    string sql = migration.down == null ? ReadMinds(migration) : GetCommand(migration.up);
                    string derived = migration.down == null ? "(DERIVED) " : string.Empty;
                    Log("{0}{1}", derived, sql);
                    migration_name = (--_currentVersion) == -1 ? "" : Migrations.Keys.ElementAt(_currentVersion);
                    if (execute)
                    {
                        _db.Execute(sql);
                        _db.Execute(string.Format("UPDATE SchemaInfo SET Version = '{0}'", migration_name));
                    }
                    Log("----------------------------------------------------\r\n");
                }
                #endregion
            }
            else
                Log("Already in version {0}. Nothing to be done.", to);
            if (!execute)
                _currentVersion = current;
        }
        
        /// <summary>
        /// This is where the shorthand types are deciphered. Fix/love/tweak as you will
        /// </summary>
        private string SetColumnType(string colType) {
            if (File.Exists(_datatypes))
                using (var t = new StreamReader(_datatypes))
                {
                    var bits = t.ReadToEnd();
                    JavaScriptSerializer deserializer = new JavaScriptSerializer();
                    Dictionary<string, string> dt = (Dictionary<string, string>)deserializer.Deserialize(bits, typeof(Dictionary<string, string>));
                    if (dt.ContainsKey(colType))
                        return dt[colType];
                 }
            return colType;
        }

        /// <summary>
        /// Build a list of columns from the past-in array in the JSON file
        /// </summary>
        private string BuildColumnList(dynamic columns) {
            //holds the output
            var sb = new System.Text.StringBuilder();
            var counter = 0;
            foreach (dynamic col in columns) {
                //name
                sb.AppendFormat(", [{0}] ", col.name);

                //append on the type. Don't do this in the formatter since the replacer might return no change at all
                sb.Append(SetColumnType(col.type));

                //nullability - don't set if this is the Primary Key
                if (col.type != "pk") {
                    if (col.nullable != null) {
                        if (col.nullable) {
                            sb.Append(" NULL ");
                        } else {
                            sb.Append(" NOT NULL ");
                        }
                    } else {
                        sb.Append(" NULL ");
                    }
                }
                if(col.def != null){
                    sb.AppendFormat(" DEFAULT {0} ",col.def);
                }
                counter++;
                //this format will indent the column
                if (counter < columns.Count) {
                    sb.Append("\r\n\t");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Strip out the leading comma. Wish there was a more elegant way to do this 
        /// and no, Regex doesn't count
        /// </summary>
        private string StripLeadingComma(string columns) {
            if (columns.StartsWith(", ")) {
                return columns.Substring(2, columns.Length - 2);
            }
            return columns;
        }

        /// <summary>
        /// create unique name for index based on table and columns specified
        /// </summary>
        private string CreateIndexName(dynamic ix) {
            var sb = new System.Text.StringBuilder();
            foreach (dynamic c in ix.columns) {
                sb.AppendFormat("{1}{0}", c.Replace(" ", "_"), (sb.Length == 0 ? "" : "_")); // ternary to only add underscore if not first iteration
            }
            return string.Format("IX_{0}_{1}", ix.table_name, sb.ToString());
        }

        /// <summary>
        /// create string for columns
        /// </summary>
        private string CreateIndexColumnString(dynamic columns) {
            var sb = new System.Text.StringBuilder();
            foreach (dynamic c in columns) {
                sb.AppendFormat("{1} [{0}] ASC", c, (sb.Length == 0 ? "" : ",")); // ternary to only add comma if not first iteration
            }
            return sb.ToString();
        }

        /// <summary>
        /// This is the main "builder" of the DDL SQL and it's tuned for SQL CE. 
        /// The idea is that you build your app using SQL CE, then upgrade it to SQL Server when you need to
        /// </summary>
        public string GetCommand(dynamic op) {
            //the "op" here is an "up" or a "down". It's dynamic as that's what the JSON parser
            //will return. The neat thing about this parser is that the dynamic result will
            //return null if the key isn't present - so it's a simple null check for the operations/keys we need.
            //this will allow you to expand and tweak this migration stuff as you like
            var sb = new StringBuilder();
            var pkName = "Id";
            //what are we doing?

            if (op == null) {
                return "-- no DOWN specified. If this is a CREATE table or ADD COLUMN - it will be generated for you";
            }

            if (op.GetType() == typeof(string)) {
                return SetColumnType(op).Replace("{", "").Replace("}", "");
            }

            //CREATE
            if (op.create_table != null)  //(DynamicExtentions.HasProperty(op, "create_table"))
            {
                var columns = BuildColumnList(op.create_table.columns);

                //add some timestamps?
                if (op.create_table.timestamps != null) {
                    columns += "\n\t, CreatedOn datetime DEFAULT getdate() NOT NULL\n\t, UpdatedOn datetime DEFAULT getdate() NOT NULL";
                }

                //make sure we have a PK :)
                if (!columns.Contains("PRIMARY KEY") & !columns.Contains("IDENTITY")) {
                    columns = "Id int PRIMARY KEY IDENTITY(1,1) NOT NULL \n\t" + columns;
                } else {
                    foreach (var col in op.create_table.columns) {
                        if (col.type.ToString() == "pk") {
                            pkName = col.name;
                            break;
                        }
                    }
                }
                columns = StripLeadingComma(columns);
                sb.AppendFormat("CREATE TABLE [{0}]\r\n\t ({1});", op.create_table.name, columns);

                //DROP 
            } 
            
            if (op.drop_table != null) {
                sb.Append( "DROP TABLE " + op.drop_table+";");
                //ADD COLUMN
            } 
            if (op.add_column != null) {
                sb.AppendFormat("ALTER TABLE [{0}] ADD {1}; ", op.add_column.table, StripLeadingComma(BuildColumnList(op.add_column.columns)));
                //DROP COLUMN
            } 
            
            if (op.remove_column != null) {
                sb.AppendFormat("ALTER TABLE [{0}] DROP COLUMN {1};", op.remove_column.table, op.remove_column.name);
                //CHANGE
            } 
            if (op.change_column != null) {
                sb.AppendFormat(
                    "ALTER TABLE [{0}] ALTER COLUMN {1};", op.change_column.table, StripLeadingComma(BuildColumnList(op.change_column.columns)));
            } 
            if (op.add_index != null) {
                sb.AppendFormat(
                    "CREATE NONCLUSTERED INDEX [{0}] ON [{1}] ({2} );",
                    CreateIndexName(op.add_index),
                    op.add_index.table_name,
                    CreateIndexColumnString(op.add_index.columns));
                //REMOVE INDEX
            } 
            if (op.remove_index != null) {
                sb.AppendFormat("DROP INDEX {0}.{1};", op.remove_index.table_name, CreateIndexName(op.remove_index));
            } 
            if (op.foreign_key != null) {
                string toColumn = op.foreign_key.to_column ?? op.foreign_key.from_column;

                var sql = @"ALTER TABLE {1}  WITH NOCHECK ADD  
CONSTRAINT [FK_{1}_{0}] FOREIGN KEY([{3}])
REFERENCES {0} ([{2}]);";
                sb.AppendFormat(sql, op.foreign_key.from_table,
                    op.foreign_key.to_table, op.foreign_key.from_column, toColumn);
            } 
            if (op.drop_foreign_key != null) {
                sb.AppendFormat("ALTER TABLE {0} DROP CONSTRAINT [FK_{0}_{1}];", op.drop_foreign_key.from_table, op.drop_foreign_key.to_table);
            }
            if (op.execute != null) {
                if (!String.IsNullOrEmpty(op.execute)) {
                    string sql = op.execute;
                    if (!sql.EndsWith(";"))
                        sql += ";";
                    sql += "\r\n";
                    sb.AppendLine(sql);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// This is the migration file loader. It uses a SortedDictionary that will sort on the key (which is the file name). 
        /// So be sure to name your file with a descriptive, sortable name. A good way to do this is the year_month_day_time:
        /// 2011_04_23_1233.yml
        /// </summary>
        private SortedList<string, dynamic> LoadMigrations(string migrationPath)
        {
            //read in the files in the db/migrations directory
            var migrationDir = new System.IO.DirectoryInfo(migrationPath);
            var result = new SortedList<string, dynamic>();
            _invalid.Clear();

            var files = migrationDir.GetFiles();
            foreach (var file in files) {
                using (var t = new StreamReader(file.FullName)) {
                    var bits = t.ReadToEnd();
                     string mig = Path.GetFileNameWithoutExtension(file.FullName);
                    //Uh oh! Did you get an error? JSON can be tricky - you have to be sure you quote your values
                    //as javascript only recognizes strings, booleans, numerics, or arrays of those things.
                    //if you always use a string.
                    try
                    {
                        dynamic decoded = JsonHelper.Decode(bits); //new JsonReader().Read(bits);
                        result.Add(mig, decoded);
                    }
                    catch (ArgumentException)
                    {
                        _invalid.Add(mig);
                        result.Add(mig, "");
                    }
                }
            }
            return result;
        }


        /// <summary>
        /// This loads up a special table that keeps track of what version your DB is on. It's one table with one field
        /// </summary>
        private void EnsureSchema(Database db) {
            //does schema info exist?
            int exists = (int)db.QueryValue("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES where TABLE_NAME='SchemaInfo'");
            if (exists == 0) {
                db.Execute("CREATE TABLE SchemaInfo (Version VARCHAR(255))");
                db.Execute("INSERT INTO SchemaInfo(Version) VALUES('')");
            }
        }

        private void CheckForExecute(Database db, dynamic op) {
            if (op.GetType() != typeof(string)) {
                if (op.execute != null) {
                    if (!String.IsNullOrEmpty(op.execute)) {
                        db.Execute(op.execute);
                    }
                }
            }
        }

        /// <summary>
        /// If a "down" isn't declared, this handy function will try and figure it out for you
        /// </summary>
        private string ReadMinds(dynamic migration) {
            string result = string.Empty;
            //CREATE
            if (migration.up.create_table != null) {
                result = string.Format("DROP TABLE [{0}]", migration.up.create_table.name);
                //DROP COLUMN
            } else if (migration.up.add_column != null) {
                result = string.Format("ALTER TABLE [{0}] DROP COLUMN {1}", migration.up.add_column, migration.up.add_column.columns[0].name);
            } else if (migration.up.add_index != null) {
                // DROP INDEX
                result = string.Format("DROP INDEX {0}.{1}", migration.up.add_index.table_name, CreateIndexName(migration.up.add_index));
            } else if (migration.up.foreign_key != null) {
                // DROP FK
                result = string.Format("ALTER TABLE {1} DROP CONSTRAINT [FK_{1}_{0}]", migration.up.foreign_key.from_table, migration.up.foreign_key.to_table);
            }
            
            return result;
        }
    }
}