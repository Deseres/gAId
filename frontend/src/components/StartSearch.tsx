import { useEffect, useRef } from 'react';
import { useMapsLibrary } from '@vis.gl/react-google-maps';
import type { Place } from '../types';

export default function StartSearch({ onSelect }: { onSelect: (place: Place) => void }) {
  const places = useMapsLibrary('places');
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!places || !container.current) return;

    const input = new places.PlaceAutocompleteElement({});
    container.current.appendChild(input);

    const handleSelect = async (event: any) => {
      const place = event.placePrediction.toPlace();
      await place.fetchFields({ fields: ['id', 'displayName', 'location'] });
      
      if (place.location) {
        onSelect({
          name: place.displayName ?? '',
          google_place_id: place.id,
          lat: place.location.lat(),
          lng: place.location.lng(),
        });
      }
    };

    input.addEventListener('gmp-select', handleSelect);

    return () => {
      input.removeEventListener('gmp-select', handleSelect);
      input.remove();
    };
  }, [places, onSelect]);

  return <div ref={container} className="w-full bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden [&>gmp-place-autocomplete]:w-full" />;
}