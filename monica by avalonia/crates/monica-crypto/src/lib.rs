// Unsafe is confined to the FFI boundary: raw pointers from C# are converted to
// slices here, then all downstream work goes through safe argon2 crate APIs.

use argon2::{Algorithm, Argon2, Params, Version};
use std::os::raw::c_uint;

/// # Safety
/// `input` must point to `length` readable bytes. Free the returned string once with `monica_lan_free`.
#[no_mangle]
pub unsafe extern "C" fn monica_lan_request(
    input: *const u8,
    length: usize,
) -> *mut std::ffi::c_char {
    let response = if input.is_null() {
        monica_lan_sync::request_json("")
    } else if length > monica_lan_sync::MAX_REQUEST_BYTES {
        // Enforce the same bound as the dispatcher before dereferencing the input.
        r#"{"ok":false,"error":"request_too_large"}"#.to_owned()
    } else {
        let bytes = unsafe { std::slice::from_raw_parts(input, length) };
        monica_lan_sync::request_json(std::str::from_utf8(bytes).unwrap_or(""))
    };
    std::ffi::CString::new(response)
        .map(std::ffi::CString::into_raw)
        .unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `value` must be null or an unfreed result from `monica_lan_request`.
#[no_mangle]
pub unsafe extern "C" fn monica_lan_free(value: *mut std::ffi::c_char) {
    if !value.is_null() {
        drop(unsafe { std::ffi::CString::from_raw(value) });
    }
}

pub const OUTPUT_LEN: usize = 32;

#[repr(C)]
pub enum KdfError {
    Success = 0,
    NullPointer = 1,
    ZeroIterations = 2,
    ZeroParallelism = 3,
    InvalidParams = 4,
    DerivationFailed = 5,
}

#[no_mangle]
pub extern "C" fn monica_argon2id(
    password_ptr: *const u8,
    password_len: c_uint,
    salt_ptr: *const u8,
    salt_len: c_uint,
    iterations: c_uint,
    memory_kib: c_uint,
    parallelism: c_uint,
    output_ptr: *mut u8,
) -> KdfError {
    if password_ptr.is_null() || salt_ptr.is_null() || output_ptr.is_null() {
        return KdfError::NullPointer;
    }
    if iterations == 0 {
        return KdfError::ZeroIterations;
    }
    if parallelism == 0 {
        return KdfError::ZeroParallelism;
    }

    let password = unsafe { std::slice::from_raw_parts(password_ptr, password_len as usize) };
    let salt = unsafe { std::slice::from_raw_parts(salt_ptr, salt_len as usize) };
    let output = unsafe { std::slice::from_raw_parts_mut(output_ptr, OUTPUT_LEN) };

    let params = match Params::new(memory_kib, iterations, parallelism, Some(OUTPUT_LEN)) {
        Ok(p) => p,
        Err(_) => return KdfError::InvalidParams,
    };

    let argon2 = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    match argon2.hash_password_into(password, salt, output) {
        Ok(()) => KdfError::Success,
        Err(_) => KdfError::DerivationFailed,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lan_request_bytes(input: &[u8]) -> String {
        let response = unsafe { monica_lan_request(input.as_ptr(), input.len()) };
        assert!(!response.is_null());
        let result = unsafe { std::ffi::CStr::from_ptr(response) }
            .to_str()
            .unwrap()
            .to_owned();
        unsafe { monica_lan_free(response) };
        result
    }

    fn sized_lan_request(bytes: usize) -> Vec<u8> {
        let prefix = br#"{"operation":"replica_close","handle":0,"padding":""#;
        let suffix = br#""}"#;
        let mut input = prefix.to_vec();
        input.resize(bytes - suffix.len(), b'x');
        input.extend_from_slice(suffix);
        input
    }

    #[test]
    fn lan_ffi_accepts_requests_through_the_documented_eight_mib_limit() {
        for size in [32 * 1024 + 1, 8 * 1024 * 1024] {
            let response = lan_request_bytes(&sized_lan_request(size));
            assert!(
                response.contains("\"ok\":true"),
                "Request of {size} bytes was rejected: {response}"
            );
        }
    }

    #[test]
    fn lan_ffi_reports_oversized_requests_without_changing_invalid_json_errors() {
        let response = lan_request_bytes(&sized_lan_request(8 * 1024 * 1024 + 1));
        assert!(response.contains("\"error\":\"request_too_large\""));
        assert!(!response.contains("padding"));
        assert!(lan_request_bytes(b"invalid-json").contains("\"error\":\"invalid_request\""));
    }

    fn decode_hex(value: &str) -> Vec<u8> {
        assert_eq!(value.len() % 2, 0);
        value
            .as_bytes()
            .chunks_exact(2)
            .map(|pair| {
                let pair = std::str::from_utf8(pair).expect("test vector must be ASCII");
                u8::from_str_radix(pair, 16).expect("test vector must be valid hex")
            })
            .collect()
    }

    #[test]
    fn argon2id_v13_matches_known_vector() {
        let password = b"password";
        let salt = b"somesalt";
        let mut output = [0_u8; OUTPUT_LEN];

        let result = monica_argon2id(
            password.as_ptr(),
            password.len() as c_uint,
            salt.as_ptr(),
            salt.len() as c_uint,
            2,
            32,
            1,
            output.as_mut_ptr(),
        );

        assert!(matches!(result, KdfError::Success));
        let expected =
            decode_hex("31111cc053ba0a799c0884148fd7ec9dc3631f3e8cf476cca9521d4ccc5136e8");
        assert_eq!(output.as_slice(), expected.as_slice());
    }

    #[test]
    fn argon2id_rejects_zero_iterations() {
        let password = b"password";
        let salt = b"somesalt";
        let mut output = [0_u8; OUTPUT_LEN];

        let result = monica_argon2id(
            password.as_ptr(),
            password.len() as c_uint,
            salt.as_ptr(),
            salt.len() as c_uint,
            0,
            32,
            1,
            output.as_mut_ptr(),
        );

        assert!(matches!(result, KdfError::ZeroIterations));
    }

    #[test]
    fn argon2id_rejects_null_pointers() {
        let mut output = [0_u8; OUTPUT_LEN];

        let result = monica_argon2id(
            std::ptr::null(),
            0,
            std::ptr::null(),
            0,
            2,
            32,
            1,
            output.as_mut_ptr(),
        );

        assert!(matches!(result, KdfError::NullPointer));
    }
}
