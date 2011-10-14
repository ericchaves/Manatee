using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Manatee;
using System.IO;
using System.Text.RegularExpressions;

namespace VidPub.Tasks {
    class Program {
        static Migrator _development;
        static Migrator _test;
        static Migrator _production;
        //this will allow you to sync a test DB with your dev DB by running the same migrations
        //there
        static bool _syncTestDB = false;

        private static string _migration_dir;
        private static string _project_dir;
        private static string _datatypes;

        static void Initialize(string projectDir)
        {
            // Test if there is an app.config or web.config in Working Directory.
            // If so bind the configuration to them. This allows Manatee to be used as external tool 
            // and have it load the connection strings from the projects configuration file.
            _project_dir = projectDir;
            if (File.Exists(Path.Combine(_project_dir, "app.config")))
                AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", Path.Combine(_project_dir, "app.config"));
            else if (File.Exists(Path.Combine(_project_dir, "web.config")))
                AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", Path.Combine(_project_dir, "web.config"));
            
            _datatypes = Path.Combine(_project_dir, "datatypes.yml");
            if (!File.Exists(_datatypes))
                using (StreamWriter outfile = new StreamWriter(_datatypes))
                {
                    outfile.Write("{ \r\n  'pk': 'int PRIMARY KEY IDENTITY(1,1)',\r\n  'money': 'decimal(8,4)',\r\n  'date': 'datetime',\r\n  'string': 'nvarchar(255)',\r\n  'boolean': 'bit',\r\n  'text': 'nvarchar(MAX)',\r\n  'guid': 'uniqueidentifier'\r\n}");
                }

            _migration_dir = Path.Combine(_project_dir, "Migrations");
            if (!Directory.Exists(_migration_dir))
                Directory.CreateDirectory(_migration_dir);
            Console.WriteLine("Working in directory:\t{0}",_project_dir);
            Reload();
        }

        static string[] _args;
        static void Main(string[] args)
        {
            _args = args;
            Initialize(Directory.GetCurrentDirectory());
            SayHello();
        }

        static long GetNameStub() {
            return long.Parse(DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        }
        static void Generate(string name) {
            //drop the name to lower, add _'s, and an extension
            var splits = name.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var fileName = name.ToLower().Replace(" ", "_") + ".yml";
            var template = Templates.Blank;
            
            //timestamp it
            var formattedName = string.Format("{0}_{1}", GetNameStub(), fileName);
            if (fileName.StartsWith("create")) {
                template = Templates.CreateTable;
                var tableName = name.Replace("create_", "").Replace("Create_","");
                template = template.Replace("my_table", tableName);
            } else if (fileName.StartsWith("add")) {
                template = Templates.AddColumn;

            } else if (fileName.StartsWith("index")) {
                template = Templates.AddIndex;

            } else if (fileName.StartsWith("fk") || fileName.StartsWith("foreign_key")) {
                template = Templates.FK;

            }
            var migrationPath = Path.Combine(LocateMigrations(),formattedName);
            using (var stream = new FileStream(migrationPath, FileMode.Create)) {
                var chars = template.ToCharArray();
                var bits = new ASCIIEncoding().GetBytes(chars);
                stream.Write(bits,0,bits.Length);
            }
            Reload();
        }

        static void Reload()
        {
            _development = new Migrator(_datatypes, _migration_dir, "development");
            _test = new Migrator(_datatypes, _migration_dir, "test", silent: true);
            _production = new Migrator(_datatypes, _migration_dir, "production");
            _production.Reload();
            if (_development.HasErrors)
                Console.WriteLine("*** Invalid migrations found ***.\r\nList migrations and look for those suffixed with !!");
        }
        static int WhichVersion(string command) {
            int result = -1;
            var stems = command.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < stems.Length; i++) {
                var bit = stems[i];
                if (bit == "/v")
                    int.TryParse(stems[i + 1], out result);
            }

            return result;
        }
        static bool ShouldExecute(string command) {
            var stems = command.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < stems.Length; i++) {
                var bit = stems[i];
                if (bit == "/p")
                    return false;
            }

            return true ;
        }
        static void DecideWhatToDo(string command){
            int version = WhichVersion(command);
            bool shouldExecute = ShouldExecute(command);
            if (command.StartsWith("cd "))
            {
                string[] cd = command.Split(new char[] { ' ' });
                if (cd.Length > 1)
                    Initialize(Path.GetFullPath(cd[1]));
            }else if(command.StartsWith("up")){
                if (version < 0)
                    version = _development.LastVersion;
                //roll it to the top
                _development.Migrate(version, execute: shouldExecute);
                if(_syncTestDB)
                    _test.Migrate(version, execute: shouldExecute);

            }else if(command.StartsWith("g") || command.StartsWith("c")){
                Console.WriteLine("Generating a Migration...");
                var name = command.Replace("g ", "").Replace("c ", "");
                Generate(name);

            }else if(command.StartsWith("down") || command.StartsWith("back") || command.StartsWith("rollback")){
                //go back one if the version isn't specified
                if (version < 0)
                    version = _development.CurrentVersion -1 ;
                _development.Migrate(version, execute: shouldExecute);
                if (_syncTestDB)
                    _test.Migrate(version, execute: shouldExecute);
            } else if (command.StartsWith("migrate")) {
                if (version < 0)
                    version = _development.LastVersion;
                _development.Migrate(version, execute: shouldExecute);
                if (_syncTestDB)
                    _test.Migrate(version, execute: shouldExecute);
            } else if (command.StartsWith("exit") || command.StartsWith("quit")) {
                Environment.Exit(1);
                return;
            }else if(command.StartsWith("list")){
                ListMigrations(command);
            } else if (command.StartsWith("push")) {
                //send it to production
                _production.Migrate();
            }else if (command.StartsWith("reload")||command.Equals("r")){
                Reload();
            }else{
                HelpEmOut();
            }
            Console.WriteLine("Done! What next?");
            var cmd = Console.ReadLine();
            DecideWhatToDo(cmd);
            
        }
        static void ListMigrations(string command){
            string[] args = command.Split(new char[]{' '});
            if (args.Length == 1)
                _development.ListMigrations("development");
            else if (args[1].StartsWith("p"))
                _production.ListMigrations("production");
            else if (args[1].StartsWith("t"))
                _test.ListMigrations("test");
            else
                _development.ListMigrations("development");
        }

        static void HelpEmOut(){
            Console.WriteLine("You can say 'up', 'down', or 'migrate' with some arguments. Those arguments are:");
            Console.WriteLine(" ... /v - this is the version number to go up or down to. To wipe our your DB, /v 0");
            Console.WriteLine(" ... /p - Print out the commands only");
            Console.WriteLine(" ... 'cd {path}' will change working directory.");
            Console.WriteLine(" ... 'back' or rollback goes back a single version");
            Console.WriteLine(" ... 'up' will run every migration not run");
            Console.WriteLine(" ... 'exit' or 'quit' will... well you know.");
            Console.WriteLine(" ... 'list' will roll out a list of all the migrations");
            Console.WriteLine(" ... 'create', 'generate', or just 'c' or 'g' stub out a template for you and stick it in your migrations directory");
            Console.WriteLine(" ... 'push' will send your database changes up to your Production box");
            Console.WriteLine(" ...  When you generate a migration, be sure to use good naming - I'll do my best to stup it out for you.");
            Console.WriteLine(" ...  - for instance, if you use a name like 'create_mytable' - I'll know to stub out create syntax for you");
            Console.WriteLine(" ...  - the trick is to start the name with the operation. If you do, I'll do the right thing");
            Console.WriteLine(" ...  - create_ will do a create table, add_ will add a column, index_ will create an index template, fk_ will create an FK template");
            Console.WriteLine(" ...  - finally - if you name it with _'s, I'll do my best to figure out a table, column, or index name");
            Console.WriteLine("----------------------------------------------------------------------------------");
            Console.WriteLine("^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^");
            var command = Console.ReadLine();
            DecideWhatToDo(command);
       }
        static void SayHello() {
            Console.WriteLine("Manatee - Migrations for .NET");
            Console.WriteLine("Current DB Version: {0}",_development.CurrentVersion);
            Console.WriteLine("You have {0} migrations with {1} un-run. Type 'list' to see more details", _development.LastVersion, _development.LastVersion - _development.CurrentVersion);
            Console.WriteLine(">> (type 'h' or 'help' for assistance)");
            var command = Console.ReadLine();
            DecideWhatToDo(command);
        }
        static string LocateMigrations() {
            return _migration_dir;
        }
    }
}
