# Builds Project Files/Source/Console/Console.DefaultLayout.cs from a Thetis DB: panel layout
# (State) and all graphics settings (Setup Display/Appearance tabs, GPU waterfall tables, 3D
# panadapter). Close Thetis first so the DB is saved, then run:
#   python tools/gen_default_look.py [path/to/database.xml]
# Without an argument it reads the active DB of SDR-VST3-x64 (dbman_settings.json).
import sys, re, glob, html, os, json
src_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Project Files', 'Source', 'Console')
if len(sys.argv) > 1:
    db_path = sys.argv[1]
else:
    db_dir = os.path.join(os.environ['APPDATA'], 'OpenHPSDR', 'SDR-VST3-x64', 'DB')
    db_path = os.path.join(db_dir, json.load(open(os.path.join(db_dir, 'dbman_settings.json'), encoding='utf-8-sig'))['ActiveDB_File'])
print(db_path)
db = open(db_path, encoding='utf-8-sig').read()

def table(name):
    xml = name if not name[0].isdigit() else '_x00%02x_' % ord(name[0]) + name[1:]
    return [(html.unescape(k), html.unescape(v)) for k, v in
            re.findall(r'<' + re.escape(xml) + r'>\s*<Key>([^<]*)</Key>\s*<Value>([^<]*)</Value>\s*</' + re.escape(xml) + '>', db)]

state = [(k, v) for k, v in table('State') if k.startswith('NativePanel') or k in ('console_width', 'console_height')]

src = open(os.path.join(src_dir, 'setup.designer.cs'), encoding='utf-8-sig').read()
for f in glob.glob(os.path.join(src_dir, 'Setup.*.cs')) + [os.path.join(src_dir, 'setup.cs')]:
    src += open(f, encoding='utf-8-sig').read()
parent = {}
for p, c in re.findall(r'(?:this\.)?(\w+)\.Controls\.Add\((?:this\.)?(\w+)\)', src):
    parent.setdefault(c, p)
TABS = {'tpDisplay', 'tpDisplayGeneral', 'tpDisplayTop', 'tpDisplayBottom', 'tpDisplayTransmit',
        'tpAppearanceGeneral', 'tpAppearanceDisplay', 'tcAppearanceTXDisplay', 'tpGradients',
        'tpAppearanceMeter', 'tpAppearanceCollapsible', 'tpLegacyItems'}
SKIP = {'txtCallsignBackground', 'chkANAN8000DLEDisplayVoltsAmps'}   # not graphics / radio specific
def in_tabs(c):
    for _ in range(60):
        if c not in parent: return False
        c = parent[c]
        if c in TABS: return True
    return False
options = [(k, v) for k, v in table('Options') if in_tabs(k) and k not in SKIP]

tables = {
    'State': state,
    'Options': options,
    'GPUWaterfallRuntime': table('GPUWaterfallRuntime'),
    'GPUWaterfallUI': table('GPUWaterfallUI'),
    '3DPanadapter': [(k, v) for k, v in table('3DPanadapter') if k not in ('Top', 'Left')],
}
# flat 3D and the sphere keep separate sets; the mode now on lives only in 3DPanadapter
cur_sphere = dict(tables['3DPanadapter']).get('chk3DSphere') == 'True'
for name, is_sphere in (('3DPanadapterFlat', False), ('3DPanadapterSphere', True)):
    rows = tables['3DPanadapter'] if is_sphere == cur_sphere else [(k, v) for k, v in table(name) if k not in ('Top', 'Left')]
    if rows: tables[name] = rows
def cs(s): return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'
body = []
for name, rows in tables.items():
    assert rows, name
    body.append(f'            {{ {cs(name)}, new Dictionary<string, string>\n                {{')
    body += [f'                    {{ {cs(k)}, {cs(v)} }},' for k, v in rows]
    body.append('                } },')
out = '''using System.Collections.Generic;

namespace Thetis
{
    // Default look of a new install, taken from the 3Z9AM station setup (generated from its DB):
    // Expand/Collapse panel layout and window size (State), every graphics setting on Setup >
    // Display and Appearance (Options), the GPU waterfall and the 3D panadapter. Written only
    // when the DB has no panel layout yet (new install / reset DB), so an existing user's
    // settings are never touched. Options are not written here: an empty Options table is how
    // Setup recognises a new DB, so getOptions applies them after its own defaults.
    public partial class Console
    {
        private static readonly Dictionary<string, Dictionary<string, string>> DefaultLook = new Dictionary<string, Dictionary<string, string>>
        {
''' + '\n'.join(body) + '''
        };

        internal static Dictionary<string, string> DefaultLookOptions() => new Dictionary<string, string>(DefaultLook["Options"]);
        internal static Dictionary<string, string> DefaultLookSphere() => new Dictionary<string, string>(DefaultLook["3DPanadapterSphere"]);

        private static void seedDefaultLayout()
        {
            foreach (string key in DB.GetVarsDictionary("State").Keys)
                if (key.StartsWith("NativePanel")) return;

            foreach (KeyValuePair<string, Dictionary<string, string>> table in DefaultLook)
            {
                if (table.Key == "Options") continue;
                Dictionary<string, string> saved = DB.GetVarsDictionary(table.Key);
                foreach (KeyValuePair<string, string> kvp in table.Value)
                    saved[kvp.Key] = kvp.Value;
                DB.SaveVarsDictionary(table.Key, ref saved, true);
            }
        }
    }
}
'''
open(os.path.join(src_dir, 'Console.DefaultLayout.cs'), 'w', encoding='utf-8', newline='\r\n').write(out)
print({k: len(v) for k, v in tables.items()})
