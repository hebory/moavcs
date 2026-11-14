use std::{env, fs, path::PathBuf};
fn main() {
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap())
        .join("../../assets/explorer-overlays");
    let icons = ["conflict.ico", "modified.ico", "untracked.ico"];
    let mut source = "#include <windows.h>\n".to_string();
    for (index, icon) in icons.into_iter().enumerate() {
        source.push_str(&format!(
            "{} ICON \"{}\"\n",
            101 + index,
            root.join(icon).to_string_lossy().replace('\\', "/")
        ));
        println!("cargo:rerun-if-changed=../../assets/explorer-overlays/{icon}");
    }
    let file = PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("overlays.rc");
    fs::write(&file, source).unwrap();
    embed_resource::compile(&file, embed_resource::NONE)
        .manifest_required()
        .unwrap();
}
