#include <CLibUtilsQTR/PresetHelpers/YAMLTemplates.hpp>
#include <yaml-cpp/emitfromevents.h>
#include <algorithm>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <vector>

void EmitExpanded(YAML::EmitFromEvents& output, const YAML::Node& node, std::vector<YAML::Node>& ancestors) {
    if (node.IsScalar()) {
        output.OnScalar(node.Mark(), node.Tag() == "!" ? "tag:yaml.org,2002:str" : node.Tag(),
                        YAML::NullAnchor, node.Scalar());
    } else if (node.IsNull()) {
        output.OnNull(node.Mark(), YAML::NullAnchor);
    } else {
        if (std::any_of(ancestors.begin(), ancestors.end(), [&](const auto& ancestor) { return ancestor.is(node); })) {
            throw YAML::RepresentationException(node.Mark(), "Circular YAML alias");
        }
        ancestors.push_back(node);
        if (node.IsSequence()) {
            output.OnSequenceStart(node.Mark(), node.Tag(), YAML::NullAnchor, YAML::EmitterStyle::Block);
            for (const auto& child : node) EmitExpanded(output, child, ancestors);
            output.OnSequenceEnd();
        } else if (node.IsMap()) {
            output.OnMapStart(node.Mark(), node.Tag(), YAML::NullAnchor, YAML::EmitterStyle::Block);
            for (const auto& entry : node) {
                EmitExpanded(output, entry.first, ancestors);
                EmitExpanded(output, entry.second, ancestors);
            }
            output.OnMapEnd();
        }
        ancestors.pop_back();
    }
}

int main() try {
    auto document = YAML::Load(std::cin);
    PresetHelpers::YAML_Helpers::ResolveTemplates(document);
    YAML::Emitter output;
    YAML::EmitFromEvents events(output);
    std::vector<YAML::Node> ancestors;
    EmitExpanded(events, document, ancestors);
    if (!output.good()) throw std::runtime_error(output.GetLastError());
    std::cout << output.c_str() << '\n';
    return std::cout ? EXIT_SUCCESS : EXIT_FAILURE;
} catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return EXIT_FAILURE;
}
