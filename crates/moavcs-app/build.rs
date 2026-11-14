use std::{env, fs, path::PathBuf};
fn main() {
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap()).join("../..");
    let version = env::var("CARGO_PKG_VERSION").unwrap();
    let parts = version.split('.').collect::<Vec<_>>();
    let numeric = format!("{},{},{},0", parts[0], parts[1], parts[2]);
    let icon = root
        .join("assets/explorer-icons/moavcs.ico")
        .to_string_lossy()
        .replace('\\', "/");
    let manifest = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap())
        .join("resources/moavcs.manifest")
        .to_string_lossy()
        .replace('\\', "/");
    let source = format!(
        r#"#include <windows.h>
1 ICON "{icon}"
1 24 "{manifest}"
1 VERSIONINFO
FILEVERSION {numeric}
PRODUCTVERSION {numeric}
FILEOS VOS_NT_WINDOWS32
FILETYPE VFT_APP
BEGIN
 BLOCK "StringFileInfo" BEGIN BLOCK "040904b0" BEGIN
 VALUE "FileDescription", "MoaVCS Rust desktop client\0"
 VALUE "FileVersion", "{version}\0"
 VALUE "ProductVersion", "{version}\0"
 VALUE "ProductName", "MoaVCS\0"
 VALUE "OriginalFilename", "moavcs.exe\0"
 END END
 BLOCK "VarFileInfo" BEGIN VALUE "Translation", 0x409, 1200 END
END
"#
    );
    let file = PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("moavcs.rc");
    fs::write(&file, source).unwrap();
    embed_resource::compile(&file, embed_resource::NONE)
        .manifest_required()
        .unwrap();
    println!("cargo:rerun-if-changed=resources/moavcs.manifest");
    println!("cargo:rerun-if-changed=../../assets/explorer-icons/moavcs.ico");
}
