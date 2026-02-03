use std::env;
use std::path::PathBuf;

fn main() {
    let crate_dir = env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR not set");
    let out_dir = PathBuf::from(&crate_dir).join("include");

    std::fs::create_dir_all(&out_dir).expect("Failed to create include directory");

    let header_path = out_dir.join("lean_crypto_ffi.h");

    let config = cbindgen::Config {
        language: cbindgen::Language::C,
        include_guard: Some("LEAN_CRYPTO_FFI_H".to_string()),
        ..Default::default()
    };

    cbindgen::Builder::new()
        .with_config(config)
        .with_crate(&crate_dir)
        .generate()
        .expect("Failed to generate bindings")
        .write_to_file(header_path);

    println!("cargo:rerun-if-changed=src/lib.rs");
}
