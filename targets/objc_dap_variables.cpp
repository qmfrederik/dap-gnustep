#include "objc_dap_variables.h"

#include <dap/protocol.h>

// The implementation is already inside cppdap.lib; only its private header is vendored.
#include "third_party/cppdap/nlohmann_json_serializer.h"

#include <cstdlib>
#include <cstring>
#include <string>

char* objc_dap_make_variables_response(const objc_dap_variable* variables, size_t count)
{
    dap::VariablesResponse response;
    for (size_t i = 0; i < count; i++) {
        dap::Variable variable;
        variable.name = variables[i].name;
        variable.type = std::string(variables[i].type);
        variable.value = variables[i].value;
        variable.variablesReference = variables[i].variables_reference;
        if (variables[i].indexed_variables > 0)
            variable.indexedVariables = variables[i].indexed_variables;
        response.variables.push_back(variable);
    }

    dap::json::NlohmannSerializer serializer;
    if (!serializer.serialize(response))
        return nullptr;

    std::string body = serializer.dump();

    char* result = static_cast<char*>(std::malloc(body.size() + 1));
    if (result)
        std::memcpy(result, body.c_str(), body.size() + 1);
    return result;
}
